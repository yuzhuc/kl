//! KL core: the only trusted execution engine.
//!
//! The process boundary is JSON-RPC 2.0 over newline-delimited stdio. Turn
//! work runs on a thread so the request loop can keep serving cancellation and
//! approval responses.

mod config;

pub use config::{
    Config, ConfigError, ProviderCatalog, config_set_default_model, provider_set_api_key,
    provider_set_enabled, provider_upsert,
};

use kl_agent::{Agent, AgentConfig, AgentError, ApprovalGate, ApprovalRequest, EventSink};
use kl_audit::AuditLog;
use kl_plugin_host::{
    PluginError, PluginHost, PluginInfo, PluginKind, SidecarProvider, SidecarTool,
};
use kl_policy::{PolicyEngine, PolicyError};
use kl_protocol::{
    ApprovalDecision, ApprovalRespondParams, AuditTailParams, CORE_NAME, CORE_VERSION, CoreEvent,
    DefaultModelResult, DoctorCheck, DoctorResult, EventNotification, HandshakeParams,
    HandshakeResult, IncomingMessage, MIN_PROTOCOL_VERSION, PROTOCOL_VERSION, PingParams,
    PingResult, PluginIdParams, PluginInstallParams, PluginListResult, ProviderEnabledResult,
    ProviderKeyResult, ProviderListResult, ProviderModelSummary, ProviderSetApiKeyParams,
    ProviderSetEnabledParams, ProviderUpsertParams, ProviderUpsertResult, RequestId, RpcError,
    SessionCreateParams, SessionInfo, SessionListResult, SessionResumeParams,
    SetDefaultModelParams, TurnCancelParams, TurnCancelResult, TurnStartParams, TurnStartResult,
    error_codes, failure, failure_with_data, methods, negotiate, success, validate_envelope,
};
use kl_provider::{
    AnthropicAdapter, BoundProvider, ChatMessage, ModelMetadata, OpenAiChatAdapter,
    OpenAiResponsesAdapter, Protocol, ProtocolAdapter, Provider, ProviderRouter, ScriptedProvider,
};
use kl_sandbox::SandboxManager;
use kl_store::{SessionData, SessionMeta, SessionStore, StoreError};
use kl_tools::ToolRegistry;
use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub const FEATURES: &[&str] = &[
    "events",
    "sessions",
    "tools",
    "audit",
    "approvals",
    "providers",
    "plugins",
];

type ProviderRoutes = Vec<(ModelMetadata, Arc<dyn Provider>)>;

/// Thread-safe newline-delimited JSON writer shared with turn threads.
#[derive(Clone)]
pub struct Emitter {
    out: Arc<Mutex<Box<dyn Write + Send>>>,
}

impl Emitter {
    pub fn new(out: Box<dyn Write + Send>) -> Self {
        Self {
            out: Arc::new(Mutex::new(out)),
        }
    }

    pub fn send<T: Serialize>(&self, value: &T) -> io::Result<()> {
        let mut guard = self.out.lock().unwrap_or_else(|error| error.into_inner());
        serde_json::to_writer(&mut *guard, value)?;
        guard.write_all(b"\n")?;
        guard.flush()
    }

    pub fn respond(&self, id: RequestId, result: Value) -> io::Result<()> {
        self.send(&success(id, result))
    }

    pub fn respond_error(
        &self,
        id: Option<RequestId>,
        code: i64,
        message: impl Into<String>,
    ) -> io::Result<()> {
        self.send(&failure(id, code, message))
    }
}

#[derive(Debug, Clone)]
pub struct ServerContext {
    pub home: PathBuf,
    pub config_path: PathBuf,
    pub started_at_unix_ms: i64,
}

impl ServerContext {
    pub fn from_env() -> Self {
        Self::resolve(None, None)
    }

    pub fn resolve(config_arg: Option<PathBuf>, home_arg: Option<PathBuf>) -> Self {
        let home = home_arg
            .or_else(|| std::env::var_os("KL_HOME").map(PathBuf::from))
            .unwrap_or_else(default_home);
        let config_path = config_arg
            .or_else(|| std::env::var_os("KL_CONFIG").map(PathBuf::from))
            .unwrap_or_else(|| home.join("config.toml"));
        Self {
            home,
            config_path,
            started_at_unix_ms: unix_ms(),
        }
    }
}

fn default_home() -> PathBuf {
    let base = std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from("."));
    base.join(".kl")
}

pub fn kl_home() -> PathBuf {
    std::env::var_os("KL_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(default_home)
}

pub fn unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or(0)
}

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    #[error("config error: {0}")]
    Config(#[from] ConfigError),
    #[error("session store error: {0}")]
    Store(#[from] StoreError),
    #[error("audit error: {0}")]
    Audit(#[from] kl_audit::AuditError),
    #[error("policy error: {0}")]
    Policy(#[from] PolicyError),
    #[error("plugin error: {0}")]
    Plugin(#[from] PluginError),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
}

#[derive(Clone)]
struct EventHub {
    emitter: Emitter,
    store: Arc<SessionStore>,
    sequences: Arc<Mutex<HashMap<String, u64>>>,
}

impl EventHub {
    fn new(emitter: Emitter, store: Arc<SessionStore>) -> Self {
        Self {
            emitter,
            store,
            sequences: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    fn register(&self, session_id: &str, seq: u64) {
        self.sequences
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(session_id.to_owned(), seq);
    }

    fn emit(&self, session_id: &str, event: CoreEvent) -> Result<u64, String> {
        let seq = {
            let mut sequences = self
                .sequences
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = sequences.entry(session_id.to_owned()).or_insert(0);
            *entry += 1;
            *entry
        };
        self.store
            .append_event(session_id, seq, &event)
            .map_err(|error| error.to_string())?;
        let notification = EventNotification::new(session_id, seq, event);
        self.emitter
            .send(&notification)
            .map_err(|error| error.to_string())?;
        Ok(seq)
    }
}

struct SessionSink {
    hub: EventHub,
    session_id: String,
}

impl EventSink for SessionSink {
    fn emit(&self, event: CoreEvent) -> Result<(), AgentError> {
        self.hub
            .emit(&self.session_id, event)
            .map(|_| ())
            .map_err(AgentError::Event)
    }
}

struct ApprovalBroker {
    pending: Mutex<HashMap<String, mpsc::Sender<ApprovalDecision>>>,
    timeout: Duration,
}

impl ApprovalBroker {
    fn new(timeout: Duration) -> Self {
        Self {
            pending: Mutex::new(HashMap::new()),
            timeout,
        }
    }

    fn wait(&self, approval_id: &str, cancel: &AtomicBool) -> ApprovalDecision {
        let (sender, receiver) = mpsc::channel();
        self.pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(approval_id.to_owned(), sender);

        let started = Instant::now();
        let decision = loop {
            if cancel.load(Ordering::Relaxed) {
                break ApprovalDecision::Deny;
            }
            match receiver.recv_timeout(Duration::from_millis(100)) {
                Ok(decision) => break decision,
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    if started.elapsed() >= self.timeout {
                        break ApprovalDecision::Deny;
                    }
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    break ApprovalDecision::Deny;
                }
            }
        };

        self.pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .remove(approval_id);
        decision
    }

    fn resolve(&self, approval_id: &str, decision: ApprovalDecision) -> bool {
        let sender = self
            .pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .remove(approval_id);
        sender
            .map(|sender| sender.send(decision).is_ok())
            .unwrap_or(false)
    }

    fn deny_turn(&self, turn_id: &str) {
        let prefix = format!("{turn_id}:");
        let ids: Vec<String> = self
            .pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .keys()
            .filter(|id| id.starts_with(&prefix))
            .cloned()
            .collect();
        for id in ids {
            self.resolve(&id, ApprovalDecision::Deny);
        }
    }
}

struct CoreApprovalGate {
    broker: Arc<ApprovalBroker>,
    hub: EventHub,
    session_id: String,
    cancel: Arc<AtomicBool>,
}

impl ApprovalGate for CoreApprovalGate {
    fn request(&self, request: ApprovalRequest) -> ApprovalDecision {
        let decision = self.broker.wait(&request.approval_id, &self.cancel);
        let _ = self.hub.emit(
            &self.session_id,
            CoreEvent::ToolApprovalResolved {
                turn_id: request.turn_id,
                call_id: request.call_id,
                approval_id: request.approval_id,
                decision,
            },
        );
        decision
    }
}

struct SessionState {
    meta: SessionMeta,
    messages: Vec<ChatMessage>,
    active_turn: Option<String>,
}

struct Core {
    context: ServerContext,
    config: Config,
    catalog: ProviderCatalog,
    router: ProviderRouter,
    tools: Arc<ToolRegistry>,
    store: Arc<SessionStore>,
    audit: Arc<AuditLog>,
    sandbox: Arc<SandboxManager>,
    plugin_host: Arc<PluginHost>,
    hub: EventHub,
    sessions: Arc<Mutex<HashMap<String, SessionState>>>,
    approvals: Arc<ApprovalBroker>,
    cancels: Arc<Mutex<HashMap<String, Arc<AtomicBool>>>>,
    threads: Vec<JoinHandle<()>>,
    handshaken: bool,
}

impl Core {
    fn new(context: ServerContext, emitter: Emitter) -> Result<Self, CoreError> {
        std::fs::create_dir_all(&context.home)?;
        let config = Config::load(&context.config_path)?;
        let sandbox = Arc::new(SandboxManager::probe(&config.sandbox));
        let plugin_host = Arc::new(PluginHost::new(context.home.join("plugins"))?);
        let config_dir = Config::config_dir(&context.config_path);
        let catalog = ProviderCatalog::load(&config, &config_dir, &context.home)?;
        let routes = build_routes(&catalog, &config, &config_dir)?;
        let router = ProviderRouter::new(
            routes,
            config.agent.provider.clone(),
            config.agent.model.clone(),
        );
        for disabled in &catalog.disabled {
            router.mark_disabled(disabled);
        }
        let tools = Arc::new(ToolRegistry::builtin());
        register_plugin_adapters(&plugin_host, &router, &tools);
        let store = Arc::new(SessionStore::new(context.home.join("sessions"))?);
        let audit = Arc::new(AuditLog::open(
            context.home.join("audit"),
            config.audit.rotate_bytes,
            config.audit.keep_files,
        )?);
        let hub = EventHub::new(emitter, store.clone());
        let approval_timeout = Duration::from_secs(config.agent.approval_timeout_secs.max(1));
        Ok(Self {
            context,
            config,
            catalog,
            router,
            tools,
            store,
            audit,
            sandbox,
            plugin_host,
            hub,
            sessions: Arc::new(Mutex::new(HashMap::new())),
            approvals: Arc::new(ApprovalBroker::new(approval_timeout)),
            cancels: Arc::new(Mutex::new(HashMap::new())),
            threads: Vec::new(),
            handshaken: false,
        })
    }

    fn handle_line(&mut self, line: &str) {
        let message: IncomingMessage = match serde_json::from_str(line) {
            Ok(message) => message,
            Err(error) => {
                let _ = self.emitter().respond_error(
                    None,
                    error_codes::PARSE_ERROR,
                    format!("invalid JSON: {error}"),
                );
                return;
            }
        };

        if let Err(error) = validate_envelope(&message) {
            let _ = self
                .emitter()
                .respond_error(message.id.clone(), error.code, error.message);
            return;
        }

        let Some(id) = message.id.clone() else {
            return;
        };
        let method = message.method.clone().unwrap_or_default();
        let needs_handshake = !matches!(
            method.as_str(),
            methods::HANDSHAKE | methods::PING | methods::DOCTOR
        );
        let result = if needs_handshake && !self.handshaken {
            Err(rpc_error(
                error_codes::NOT_INITIALIZED,
                "handshake required before this method",
            ))
        } else {
            self.dispatch(&method, message.params)
        };

        match result {
            Ok(value) => {
                let _ = self.emitter().respond(id, value);
            }
            Err(error) => {
                let _ = match error.data {
                    Some(data) => self.emitter().send(&failure_with_data(
                        Some(id),
                        error.code,
                        error.message,
                        data,
                    )),
                    None => self
                        .emitter()
                        .respond_error(Some(id), error.code, error.message),
                };
            }
        }
    }

    fn emitter(&self) -> &Emitter {
        &self.hub.emitter
    }

    fn dispatch(&mut self, method: &str, params: Value) -> Result<Value, RpcError> {
        match method {
            methods::HANDSHAKE => self.handshake(params),
            methods::PING => self.ping(params),
            methods::DOCTOR => self.doctor(),
            methods::SESSION_CREATE => self.session_create(params),
            methods::SESSION_RESUME => self.session_resume(params),
            methods::SESSION_LIST => self.session_list(),
            methods::TURN_START => self.turn_start(params),
            methods::TURN_CANCEL => self.turn_cancel(params),
            methods::APPROVAL_RESPOND => self.approval_respond(params),
            methods::PROVIDER_LIST => self.provider_list(),
            methods::PROVIDER_UPSERT => self.provider_upsert(params),
            methods::PROVIDER_SET_API_KEY => self.provider_set_api_key(params),
            methods::PROVIDER_SET_ENABLED => self.provider_set_enabled(params),
            methods::CONFIG_SET_DEFAULT_MODEL => self.config_set_default_model(params),
            methods::AUDIT_TAIL => self.audit_tail(params),
            methods::CONFIG_GET => self.config_get(),
            methods::PLUGIN_LIST => self.plugin_list(),
            methods::PLUGIN_INSTALL => self.plugin_install(params),
            methods::PLUGIN_RELOAD => self.plugin_reload(params),
            methods::PLUGIN_ENABLE => self.plugin_enable(params),
            methods::PLUGIN_DISABLE => self.plugin_disable(params),
            other => Err(rpc_error(
                error_codes::METHOD_NOT_FOUND,
                format!("unknown method: {other}"),
            )),
        }
    }

    fn handshake(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: HandshakeParams = parse_params(params, "handshake")?;
        let Some(version) = negotiate(&params.protocol_versions) else {
            return Err(RpcError {
                code: error_codes::VERSION_MISMATCH,
                message: "no compatible protocol version".to_owned(),
                data: Some(json!({
                    "server_min": MIN_PROTOCOL_VERSION,
                    "server_max": PROTOCOL_VERSION,
                    "client": params.protocol_versions,
                })),
            });
        };
        self.handshaken = true;
        to_value(HandshakeResult {
            server_name: CORE_NAME.to_owned(),
            server_version: CORE_VERSION.to_owned(),
            protocol_version: version,
            min_protocol_version: MIN_PROTOCOL_VERSION,
            features: FEATURES
                .iter()
                .map(|feature| (*feature).to_owned())
                .collect(),
        })
    }

    fn ping(&self, params: Value) -> Result<Value, RpcError> {
        let params: PingParams = serde_json::from_value(params).unwrap_or_default();
        to_value(PingResult {
            pong: true,
            nonce: params.nonce,
            server_time_unix_ms: unix_ms(),
        })
    }

    fn doctor(&self) -> Result<Value, RpcError> {
        let mut checks = Vec::new();
        checks.push(DoctorCheck {
            name: "core".to_owned(),
            ok: true,
            detail: format!("{CORE_NAME} {CORE_VERSION}"),
        });
        checks.push(DoctorCheck {
            name: "protocol".to_owned(),
            ok: true,
            detail: format!("v{PROTOCOL_VERSION}"),
        });
        checks.push(DoctorCheck {
            name: "handshake".to_owned(),
            ok: self.handshaken,
            detail: if self.handshaken {
                "completed".to_owned()
            } else {
                "not yet completed".to_owned()
            },
        });
        checks.push(check_writable_dir(&self.context.home, "home_writable"));
        checks.push(check_writable_dir(
            &self.context.home.join("sessions"),
            "sessions_writable",
        ));
        checks.push(check_writable_dir(
            &self.context.home.join("audit"),
            "audit_writable",
        ));
        let audit_count = self.audit.count();
        checks.push(DoctorCheck {
            name: "audit_index".to_owned(),
            ok: audit_count.is_ok(),
            detail: match audit_count {
                Ok(count) => format!(
                    "{} records indexed in {}",
                    count,
                    self.audit.sqlite_path().display()
                ),
                Err(error) => error.to_string(),
            },
        });
        checks.push(DoctorCheck {
            name: "config".to_owned(),
            ok: true,
            detail: if self.context.config_path.exists() {
                format!(
                    "{} ({} providers)",
                    self.context.config_path.display(),
                    self.config.providers.len()
                )
            } else {
                format!("{} (defaults in use)", self.context.config_path.display())
            },
        });
        let provider_check = match self.router.get(
            Some(&self.config.agent.provider),
            Some(&self.config.agent.model),
        ) {
            Ok((provider, metadata)) => {
                let (available, detail) = provider.available();
                DoctorCheck {
                    name: "provider_active".to_owned(),
                    ok: available,
                    detail: format!("{}/{} ({detail})", metadata.provider, metadata.model),
                }
            }
            Err(error) => DoctorCheck {
                name: "provider_active".to_owned(),
                ok: false,
                detail: error.to_string(),
            },
        };
        checks.push(provider_check);
        let sandbox = self.sandbox.status();
        checks.push(DoctorCheck {
            name: "sandbox_backend".to_owned(),
            ok: sandbox.available,
            detail: format!("{}: {}", sandbox.backend, sandbox.detail),
        });
        checks.push(DoctorCheck {
            name: "sandbox_strict".to_owned(),
            ok: !sandbox.strict || sandbox.available,
            detail: if sandbox.strict {
                "strict mode requested; unavailable backends fail closed".to_owned()
            } else {
                "non-strict; policy-only fallback is allowed".to_owned()
            },
        });
        checks.push(DoctorCheck {
            name: "sandbox_z_isolation".to_owned(),
            ok: !sandbox.isolated_z_failure,
            detail: if sandbox.isolated_z_failure {
                "MXC Z: enumeration failure isolated; strict mode would fail closed".to_owned()
            } else {
                "no MXC Z: enumeration failure detected".to_owned()
            },
        });
        checks.push(DoctorCheck {
            name: "tools".to_owned(),
            ok: true,
            detail: self.tools.names().join(", "),
        });
        let plugins = self.plugin_host.list();
        let enabled_plugins: Vec<&PluginInfo> =
            plugins.iter().filter(|plugin| plugin.enabled).collect();
        let unhealthy_plugins = enabled_plugins
            .iter()
            .filter(|plugin| !plugin.healthy)
            .count();
        checks.push(DoctorCheck {
            name: "plugins".to_owned(),
            ok: unhealthy_plugins == 0,
            detail: format!(
                "{} installed, {} enabled, {} unhealthy",
                plugins.len(),
                enabled_plugins.len(),
                unhealthy_plugins
            ),
        });
        checks.push(check_shell());
        checks.push(check_git());
        checks.push(DoctorCheck {
            name: "python_dependency".to_owned(),
            ok: true,
            detail: "not used: Rust core and .NET CLI only".to_owned(),
        });
        let ok = checks.iter().all(|check| check.ok);
        to_value(DoctorResult {
            ok,
            core_version: CORE_VERSION.to_owned(),
            protocol_version: PROTOCOL_VERSION,
            checks,
        })
    }

    fn session_create(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionCreateParams = parse_params(params, "session.create")?;
        let workspace = dunce::canonicalize(&params.workspace).map_err(|error| {
            rpc_error(
                error_codes::INVALID_PARAMS,
                format!("workspace is not accessible: {error}"),
            )
        })?;
        if !workspace.is_dir() {
            return Err(rpc_error(
                error_codes::INVALID_PARAMS,
                "workspace must be a directory",
            ));
        }
        let provider_id = params
            .provider
            .unwrap_or_else(|| self.config.agent.provider.clone());
        let (provider, metadata) = self
            .router
            .get(Some(&provider_id), params.model.as_deref())
            .map_err(provider_error)?;
        check_provider_available(provider.as_ref())?;

        let data = self
            .store
            .create(
                &workspace.to_string_lossy(),
                &metadata.provider,
                &metadata.model,
            )
            .map_err(store_error)?;
        let session_id = data.meta.id.clone();
        self.hub.register(&session_id, 0);
        {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            sessions.insert(
                session_id.clone(),
                SessionState {
                    meta: data.meta.clone(),
                    messages: Vec::new(),
                    active_turn: None,
                },
            );
        }
        let _ = self.hub.emit(
            &session_id,
            CoreEvent::SessionCreated {
                session_id: session_id.clone(),
            },
        );
        to_value(session_info(&data.meta, 0))
    }

    fn session_resume(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionResumeParams = parse_params(params, "session.resume")?;
        {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            if let Some(state) = sessions.get(&params.session_id) {
                return to_value(session_info(&state.meta, state.messages.len()));
            }
        }

        let data = self.store.load(&params.session_id).map_err(store_error)?;
        let messages = data
            .messages
            .iter()
            .cloned()
            .map(|value| {
                serde_json::from_value::<ChatMessage>(value)
                    .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))
            })
            .collect::<Result<Vec<_>, _>>()?;
        let seq = self
            .store
            .event_count(&params.session_id)
            .map_err(store_error)?;
        self.hub.register(&params.session_id, seq);
        let info = session_info(&data.meta, messages.len());
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        sessions.insert(
            params.session_id,
            SessionState {
                meta: data.meta,
                messages,
                active_turn: None,
            },
        );
        to_value(info)
    }

    fn session_list(&self) -> Result<Value, RpcError> {
        let sessions = self.store.list().map_err(store_error)?;
        to_value(SessionListResult {
            sessions: sessions
                .iter()
                .map(|meta| session_info(meta, meta.message_count))
                .collect(),
        })
    }

    fn turn_start(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: TurnStartParams = parse_params(params, "turn.start")?;
        let turn_id = uuid::Uuid::new_v4().to_string();

        self.threads.retain(|handle| !handle.is_finished());

        let (metadata, workspace, history, provider) = {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let state = sessions.get_mut(&params.session_id).ok_or_else(|| {
                rpc_error(
                    error_codes::SESSION_NOT_FOUND,
                    format!("session not found: {}", params.session_id),
                )
            })?;
            if let Some(active) = &state.active_turn {
                return Err(rpc_error(
                    error_codes::TURN_IN_PROGRESS,
                    format!("turn already running: {active}"),
                ));
            }
            let provider_id = params
                .provider
                .clone()
                .unwrap_or_else(|| state.meta.provider.clone());
            let requested_model = params
                .model
                .clone()
                .or_else(|| (provider_id == state.meta.provider).then(|| state.meta.model.clone()));
            let (provider, metadata) = self
                .router
                .get(Some(&provider_id), requested_model.as_deref())
                .map_err(provider_error)?;
            check_provider_available(provider.as_ref())?;
            let workspace = state.meta.workspace.clone();
            state.active_turn = Some(turn_id.clone());
            state.meta.provider = metadata.provider.clone();
            state.meta.model = metadata.model.clone();
            (metadata, workspace, state.messages.clone(), provider)
        };

        let policy = Arc::new(
            PolicyEngine::new(PathBuf::from(&workspace), self.config.policy.clone())
                .map_err(|error| rpc_error(error_codes::INVALID_PARAMS, error.to_string()))?,
        );
        let cancel = Arc::new(AtomicBool::new(false));
        self.cancels
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(turn_id.clone(), cancel.clone());

        let session_id = params.session_id;
        let prompt = params.prompt;
        let sessions = self.sessions.clone();
        let store = self.store.clone();
        let hub = self.hub.clone();
        let audit = self.audit.clone();
        let sandbox = self.sandbox.clone();
        let tools = self.tools.clone();
        let approvals = self.approvals.clone();
        let cancels = self.cancels.clone();
        let agent_config = AgentConfig {
            max_iterations: self.config.agent.max_iterations.max(1),
            max_tool_calls_per_turn: 32,
            max_tool_output_bytes: self.config.agent.max_tool_output_bytes,
            command_timeout: Duration::from_secs(self.config.agent.command_timeout_secs.max(1)),
        };

        let thread_session = session_id.clone();
        let thread_turn = turn_id.clone();
        let thread_metadata = metadata.clone();
        let thread_cancel = cancel.clone();
        let handle = thread::spawn(move || {
            let agent = Agent::new(
                provider,
                thread_metadata.clone(),
                tools,
                policy,
                sandbox,
                audit,
                agent_config,
            );
            let sink = SessionSink {
                hub: hub.clone(),
                session_id: thread_session.clone(),
            };
            let gate = CoreApprovalGate {
                broker: approvals,
                hub: hub.clone(),
                session_id: thread_session.clone(),
                cancel: thread_cancel.clone(),
            };
            let mut history = history;
            let result = agent.run_turn(
                &thread_session,
                &thread_turn,
                &prompt,
                &mut history,
                thread_cancel.clone(),
                &sink,
                &gate,
            );
            if let Err(error) = &result {
                if !matches!(error, AgentError::Provider(_) | AgentError::MaxIterations) {
                    let _ = hub.emit(
                        &thread_session,
                        CoreEvent::TurnFailed {
                            turn_id: thread_turn.clone(),
                            message: error.to_string(),
                        },
                    );
                }
            }

            if let Ok(mut sessions) = sessions.lock() {
                if let Some(state) = sessions.get_mut(&thread_session) {
                    state.messages = history;
                    state.active_turn = None;
                    state.meta.provider = thread_metadata.provider.clone();
                    state.meta.model = thread_metadata.model.clone();
                    state.meta.status = Some(match &result {
                        Ok(turn) => turn.status.as_str().to_owned(),
                        Err(_) => "failed".to_owned(),
                    });
                    let data = SessionData {
                        meta: state.meta.clone(),
                        messages: state
                            .messages
                            .iter()
                            .map(|message| serde_json::to_value(message).unwrap_or(Value::Null))
                            .collect(),
                    };
                    let _ = store.save(&data);
                }
            }
            cancels
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .remove(&thread_turn);
        });
        self.threads.push(handle);

        to_value(TurnStartResult {
            turn_id,
            provider: metadata.provider.clone(),
            model: metadata.model.clone(),
        })
    }

    fn turn_cancel(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: TurnCancelParams = parse_params(params, "turn.cancel")?;
        let cancelled = {
            let cancels = self
                .cancels
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            if let Some(flag) = cancels.get(&params.turn_id) {
                flag.store(true, Ordering::Relaxed);
                true
            } else {
                false
            }
        };
        self.approvals.deny_turn(&params.turn_id);
        to_value(TurnCancelResult { cancelled })
    }

    fn approval_respond(&self, params: Value) -> Result<Value, RpcError> {
        let params: ApprovalRespondParams = parse_params(params, "tool.approval.respond")?;
        let accepted = self.approvals.resolve(&params.approval_id, params.decision);
        if accepted {
            to_value(kl_protocol::ApprovalRespondResult { accepted })
        } else {
            Err(rpc_error(
                error_codes::APPROVAL_NOT_FOUND,
                format!("approval not pending: {}", params.approval_id),
            ))
        }
    }

    fn provider_list(&self) -> Result<Value, RpcError> {
        let models = self
            .router
            .list()
            .into_iter()
            .map(|model| ProviderModelSummary {
                provider: model.provider,
                model: model.model,
                protocol: model.protocol.as_str().to_owned(),
                url: model.url,
                context_window: model.context_window,
                compact_threshold: model.compact_threshold,
                max_output_tokens: model.max_output_tokens,
                enabled: model.enabled,
                key_configured: model.key_configured,
                available: model.available,
                detail: model.detail,
            })
            .collect();
        to_value(ProviderListResult {
            active_provider: self.config.agent.provider.clone(),
            active_model: self.config.agent.model.clone(),
            models,
        })
    }

    fn provider_upsert(&self, params: Value) -> Result<Value, RpcError> {
        let params: ProviderUpsertParams = parse_params(params, methods::PROVIDER_UPSERT)?;
        let result: ProviderUpsertResult =
            provider_upsert(&self.context.config_path, &params).map_err(config_write_error)?;
        to_value(result)
    }

    fn provider_set_api_key(&self, params: Value) -> Result<Value, RpcError> {
        let params: ProviderSetApiKeyParams = parse_params(params, methods::PROVIDER_SET_API_KEY)?;
        let result: ProviderKeyResult =
            provider_set_api_key(&self.context.config_path, &self.context.home, &params)
                .map_err(config_write_error)?;
        to_value(result)
    }

    fn provider_set_enabled(&self, params: Value) -> Result<Value, RpcError> {
        let params: ProviderSetEnabledParams = parse_params(params, methods::PROVIDER_SET_ENABLED)?;
        let result: ProviderEnabledResult =
            provider_set_enabled(&self.context.config_path, &params).map_err(config_write_error)?;
        to_value(result)
    }

    fn config_set_default_model(&self, params: Value) -> Result<Value, RpcError> {
        let params: SetDefaultModelParams =
            parse_params(params, methods::CONFIG_SET_DEFAULT_MODEL)?;
        let result: DefaultModelResult =
            config_set_default_model(&self.context.config_path, &self.context.home, &params)
                .map_err(config_write_error)?;
        to_value(result)
    }

    fn audit_tail(&self, params: Value) -> Result<Value, RpcError> {
        let params: AuditTailParams =
            serde_json::from_value(params).unwrap_or(AuditTailParams { limit: 20 });
        let records = self
            .audit
            .tail(params.limit.clamp(1, 1000))
            .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))?;
        to_value(kl_protocol::AuditTailResult { records })
    }

    fn plugin_list(&self) -> Result<Value, RpcError> {
        let plugins = self
            .plugin_host
            .list()
            .into_iter()
            .map(to_value)
            .collect::<Result<Vec<_>, _>>()?;
        to_value(PluginListResult { plugins })
    }

    fn plugin_install(&self, params: Value) -> Result<Value, RpcError> {
        let params: PluginInstallParams = parse_params(params, "plugin.install")?;
        let info = self
            .plugin_host
            .install_from_dir(Path::new(&params.path))
            .map_err(plugin_error)?;
        self.sync_plugins();
        to_value(info)
    }

    fn plugin_reload(&self, params: Value) -> Result<Value, RpcError> {
        let params: PluginIdParams = parse_params(params, "plugin.reload")?;
        let info = self.plugin_host.reload(&params.id).map_err(plugin_error)?;
        self.sync_plugins();
        to_value(info)
    }

    fn plugin_enable(&self, params: Value) -> Result<Value, RpcError> {
        let params: PluginIdParams = parse_params(params, "plugin.enable")?;
        let info = self.plugin_host.enable(&params.id).map_err(plugin_error)?;
        self.sync_plugins();
        to_value(info)
    }

    fn plugin_disable(&self, params: Value) -> Result<Value, RpcError> {
        let params: PluginIdParams = parse_params(params, "plugin.disable")?;
        let info = self.plugin_host.disable(&params.id).map_err(plugin_error)?;
        self.sync_plugins();
        to_value(info)
    }

    fn sync_plugins(&self) {
        for info in self.plugin_host.list() {
            match info.kind {
                PluginKind::Provider => {
                    if info.enabled {
                        self.router.enable_provider(&info.id);
                        let provider = Arc::new(SidecarProvider::new(
                            self.plugin_host.clone(),
                            info.id.clone(),
                        ));
                        for model in &info.models {
                            let metadata = ModelMetadata::new(
                                info.id.clone(),
                                model.clone(),
                                Protocol::Plugin,
                                format!("plugin://{}/{model}", info.id),
                                8192,
                                0.8,
                                4096,
                            );
                            self.router.register(metadata, provider.clone());
                        }
                    } else {
                        self.router.disable_provider(&info.id);
                    }
                }
                PluginKind::Tool => {
                    if info.enabled {
                        if let Some(tool) =
                            SidecarTool::new(self.plugin_host.clone(), info.id.clone())
                        {
                            self.tools.register(Arc::new(tool));
                        }
                    } else if let Some(name) = info.tool_name {
                        self.tools.remove(&name);
                    }
                }
            }
        }
    }

    fn config_get(&self) -> Result<Value, RpcError> {
        to_value(json!({
            "path": self.context.config_path,
            "config": self.config.redacted(),
            "providers": self.catalog.redacted(),
        }))
    }

    fn shutdown(&mut self) {
        {
            let cancels = self
                .cancels
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            for flag in cancels.values() {
                flag.store(true, Ordering::Relaxed);
            }
        }
        for handle in self.threads.drain(..) {
            let _ = handle.join();
        }
    }
}

fn register_plugin_adapters(
    host: &Arc<PluginHost>,
    router: &ProviderRouter,
    tools: &Arc<ToolRegistry>,
) {
    for info in host.list() {
        if !info.enabled {
            continue;
        }
        match info.kind {
            PluginKind::Provider => {
                for model in &info.models {
                    let metadata = ModelMetadata::new(
                        info.id.clone(),
                        model.clone(),
                        Protocol::Plugin,
                        format!("plugin://{}/{model}", info.id),
                        8192,
                        0.8,
                        4096,
                    );
                    router.register(
                        metadata,
                        Arc::new(SidecarProvider::new(host.clone(), info.id.clone())),
                    );
                }
            }
            PluginKind::Tool => {
                if let Some(tool) = SidecarTool::new(host.clone(), info.id.clone()) {
                    tools.register(Arc::new(tool));
                }
            }
        }
    }
}

fn build_routes(
    catalog: &ProviderCatalog,
    config: &Config,
    config_dir: &Path,
) -> Result<ProviderRoutes, CoreError> {
    let mut routes = Vec::new();
    for provider in &catalog.providers {
        for metadata in &provider.models {
            let adapter: Arc<dyn ProtocolAdapter> = match metadata.protocol {
                Protocol::OpenAiChat => Arc::new(OpenAiChatAdapter::new(
                    metadata.url.clone(),
                    provider.api_key.clone(),
                )),
                Protocol::Anthropic => Arc::new(AnthropicAdapter::new(
                    metadata.url.clone(),
                    provider.api_key.clone(),
                )),
                Protocol::OpenAiResponses => Arc::new(OpenAiResponsesAdapter::new(
                    metadata.url.clone(),
                    provider.api_key.clone(),
                )),
                protocol => {
                    return Err(CoreError::Config(ConfigError::UnsupportedProtocol {
                        provider: metadata.provider.clone(),
                        model: metadata.model.clone(),
                        protocol: protocol.as_str().to_owned(),
                    }));
                }
            };
            let bound = Arc::new(BoundProvider::new(metadata.clone(), adapter));
            routes.push((metadata.clone(), bound as Arc<dyn Provider>));
        }
    }

    if let Some(script_path) = &config.agent.scripted_path {
        let path = Path::new(script_path);
        let path = if path.is_absolute() {
            path.to_path_buf()
        } else {
            config_dir.join(path)
        };
        if !path.exists() {
            return Err(CoreError::Config(ConfigError::ScriptedFileMissing(
                path.display().to_string(),
            )));
        }
        let scripted = ScriptedProvider::from_file("scripted", &path)
            .map_err(|error| CoreError::Config(ConfigError::Scripted(error.to_string())))?;
        let model = if config.agent.provider == "scripted" && !config.agent.model.is_empty() {
            config.agent.model.clone()
        } else {
            "scripted".to_owned()
        };
        let metadata = ModelMetadata::new(
            "scripted",
            model,
            Protocol::Scripted,
            "offline://scripted",
            8192,
            0.8,
            4096,
        );
        routes.push((metadata, Arc::new(scripted) as Arc<dyn Provider>));
    }

    Ok(routes)
}

fn session_info(meta: &SessionMeta, message_count: usize) -> SessionInfo {
    SessionInfo {
        session_id: meta.id.clone(),
        workspace: meta.workspace.clone(),
        provider: meta.provider.clone(),
        model: meta.model.clone(),
        created_at: meta.created_at.clone(),
        updated_at: meta.updated_at.clone(),
        message_count,
        status: meta.status.clone(),
        task: meta.task.clone(),
        prompt: meta.prompt.clone(),
        last_message: meta.last_message.clone(),
    }
}

fn parse_params<T: DeserializeOwned>(params: Value, method: &str) -> Result<T, RpcError> {
    serde_json::from_value(params).map_err(|error| {
        rpc_error(
            error_codes::INVALID_PARAMS,
            format!("invalid {method} params: {error}"),
        )
    })
}

fn to_value<T: Serialize>(value: T) -> Result<Value, RpcError> {
    serde_json::to_value(value)
        .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))
}

fn rpc_error(code: i64, message: impl Into<String>) -> RpcError {
    RpcError {
        code,
        message: message.into(),
        data: None,
    }
}

fn plugin_error(error: PluginError) -> RpcError {
    let code = match error {
        PluginError::NotFound(_) => error_codes::PLUGIN_NOT_FOUND,
        PluginError::PermissionDenied(_) => error_codes::PLUGIN_PERMISSION,
        _ => error_codes::PLUGIN_ERROR,
    };
    rpc_error(code, error.to_string())
}

fn config_write_error(error: ConfigError) -> RpcError {
    rpc_error(error_codes::INVALID_PARAMS, error.to_string())
}

fn provider_error(error: kl_provider::ProviderError) -> RpcError {
    RpcError {
        code: error_codes::PROVIDER_UNAVAILABLE,
        message: error.message.clone(),
        data: Some(json!({
            "kind": format!("{:?}", error.kind),
            "retryable": error.retryable,
        })),
    }
}

fn check_provider_available(provider: &dyn Provider) -> Result<(), RpcError> {
    let (available, detail) = provider.available();
    if available {
        Ok(())
    } else {
        Err(RpcError {
            code: error_codes::PROVIDER_UNAVAILABLE,
            message: format!("provider `{}` is unavailable: {detail}", provider.id()),
            data: Some(json!({"provider": provider.id()})),
        })
    }
}

fn store_error(error: StoreError) -> RpcError {
    match error {
        StoreError::NotFound(id) => rpc_error(
            error_codes::SESSION_NOT_FOUND,
            format!("session not found: {id}"),
        ),
        other => rpc_error(error_codes::INTERNAL_ERROR, other.to_string()),
    }
}

fn check_writable_dir(path: &Path, name: &str) -> DoctorCheck {
    let result = std::fs::create_dir_all(path).and_then(|()| {
        let probe = path.join(".write-probe");
        std::fs::write(&probe, b"ok")?;
        std::fs::remove_file(&probe)
    });
    DoctorCheck {
        name: name.to_owned(),
        ok: result.is_ok(),
        detail: match result {
            Ok(()) => path.display().to_string(),
            Err(error) => format!("{}: {error}", path.display()),
        },
    }
}

fn check_shell() -> DoctorCheck {
    let shell = if cfg!(windows) {
        std::env::var("ComSpec").ok()
    } else {
        std::env::var("SHELL").ok()
    };
    DoctorCheck {
        name: "shell".to_owned(),
        ok: shell.is_some(),
        detail: shell.unwrap_or_else(|| "not found".to_owned()),
    }
}

fn check_git() -> DoctorCheck {
    match std::process::Command::new("git").arg("--version").output() {
        Ok(output) if output.status.success() => DoctorCheck {
            name: "git".to_owned(),
            ok: true,
            detail: String::from_utf8_lossy(&output.stdout).trim().to_owned(),
        },
        Ok(output) => DoctorCheck {
            name: "git".to_owned(),
            ok: false,
            detail: String::from_utf8_lossy(&output.stderr).trim().to_owned(),
        },
        Err(error) => DoctorCheck {
            name: "git".to_owned(),
            ok: false,
            detail: error.to_string(),
        },
    }
}

/// Serve JSON-RPC until EOF.
pub fn run_with_io<R: BufRead>(reader: R, emitter: Emitter) -> Result<(), CoreError> {
    run_with_io_args(reader, emitter, None, None)
}

pub fn run_with_io_args<R: BufRead>(
    reader: R,
    emitter: Emitter,
    config_path: Option<PathBuf>,
    home: Option<PathBuf>,
) -> Result<(), CoreError> {
    let mut core = Core::new(ServerContext::resolve(config_path, home), emitter)?;
    for line in reader.lines() {
        match line {
            Ok(line) => {
                if !line.trim().is_empty() {
                    core.handle_line(&line);
                }
            }
            Err(error) => {
                let _ =
                    core.emitter()
                        .respond_error(None, error_codes::PARSE_ERROR, error.to_string());
            }
        }
    }
    core.shutdown();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[derive(Clone, Default)]
    struct Buffer(Arc<Mutex<Vec<u8>>>);

    impl Write for Buffer {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            self.0.lock().unwrap().extend_from_slice(bytes);
            Ok(bytes.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    fn serve(input: &str, config: Option<PathBuf>, home: PathBuf) -> Vec<Value> {
        let buffer = Buffer::default();
        run_with_io_args(
            Cursor::new(input),
            Emitter::new(Box::new(buffer.clone())),
            config,
            Some(home),
        )
        .unwrap();
        let bytes = buffer.0.lock().unwrap().clone();
        String::from_utf8(bytes)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    #[test]
    fn handshake_then_ping() {
        let home = tempfile::tempdir().unwrap();
        let output = serve(
            r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"kl.ping","params":{"nonce":"abc"}}
"#,
            None,
            home.path().to_path_buf(),
        );
        assert_eq!(output.len(), 2);
        assert_eq!(output[0]["result"]["protocol_version"], 1);
        assert_eq!(output[0]["result"]["server_name"], "kl-core");
        assert_eq!(output[1]["result"]["pong"], true);
        assert_eq!(output[1]["result"]["nonce"], "abc");
    }

    #[test]
    fn rejects_incompatible_version() {
        let home = tempfile::tempdir().unwrap();
        let output = serve(
            r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[9]}}
"#,
            None,
            home.path().to_path_buf(),
        );
        assert_eq!(output.len(), 1);
        assert_eq!(output[0]["error"]["code"], error_codes::VERSION_MISMATCH);
        assert_eq!(output[0]["error"]["data"]["server_max"], 1);
    }

    #[test]
    fn requires_handshake_for_sessions() {
        let home = tempfile::tempdir().unwrap();
        let output = serve(
            r#"{"jsonrpc":"2.0","id":1,"method":"session.list","params":{}}
"#,
            None,
            home.path().to_path_buf(),
        );
        assert_eq!(output[0]["error"]["code"], error_codes::NOT_INITIALIZED);
    }

    #[test]
    fn routes_three_native_protocols_and_redacts_keys() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(temp.path().join("providers")).unwrap();
        std::fs::write(
            temp.path().join("providers/opencode.toml"),
            r#"
api_key = "super-secret-key"
[protocols]
openai-chat = "https://gateway.example/v1/chat/completions"
anthropic = "https://gateway.example/v1/messages"
openai-responses = "https://gateway.example/v1/responses"
[[models]]
id = "gpt-5-chat"
protocol = "openai-chat"
context_window = 128000
compact_threshold = 0.8
max_output_tokens = 4096
[[models]]
id = "claude-native"
protocol = "anthropic"
context_window = 200000
compact_threshold = 0.75
max_output_tokens = 8192
[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 256000
compact_threshold = 0.9
max_output_tokens = 16384
url = "https://gateway.example/custom/responses"
"#,
        )
        .unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "opencode"
model = "gpt-5-responses"
[providers.opencode]
enabled = true
path = "providers/opencode.toml"
"#,
        )
        .unwrap();
        let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
        let input = format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"provider.list\",\"params\":{{}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\",\"provider\":\"opencode\",\"model\":\"gpt-5-responses\"}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"config.get\",\"params\":{{}}}}\n"
        );
        let output = serve(
            &input,
            Some(temp.path().join("kl.toml")),
            temp.path().join(".kl"),
        );
        let providers = &output.iter().find(|value| value["id"] == 2).unwrap()["result"];
        let models = providers["models"].as_array().unwrap();
        assert_eq!(models.len(), 3);
        assert_eq!(providers["active_provider"], "opencode");
        assert_eq!(providers["active_model"], "gpt-5-responses");
        let model = |id: &str| models.iter().find(|model| model["model"] == id).unwrap();
        assert_eq!(model("gpt-5-chat")["protocol"], "openai-chat");
        assert_eq!(model("claude-native")["protocol"], "anthropic");
        assert_eq!(model("gpt-5-responses")["protocol"], "openai-responses");
        assert_eq!(model("gpt-5-responses")["context_window"], 256000);
        let threshold = model("gpt-5-responses")["compact_threshold"]
            .as_f64()
            .unwrap();
        assert!((threshold - 0.9).abs() < 1e-6);
        assert_eq!(model("gpt-5-responses")["max_output_tokens"], 16384);
        assert_eq!(
            model("gpt-5-responses")["url"],
            "https://gateway.example/custom/responses"
        );

        let create = output.iter().find(|value| value["id"] == 3).unwrap();
        assert_eq!(create["result"]["provider"], "opencode");
        assert_eq!(create["result"]["model"], "gpt-5-responses");

        let config = output.iter().find(|value| value["id"] == 4).unwrap();
        assert!(!config.to_string().contains("super-secret-key"));
        assert!(config.to_string().contains("configured"));
    }

    #[test]
    fn disabled_provider_has_no_models_and_selection_fails() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "opencode"
model = "gpt-5-chat"
[providers.opencode]
enabled = false
path = "providers/missing.toml"
"#,
        )
        .unwrap();
        let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
        let input = format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"provider.list\",\"params\":{{}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\",\"provider\":\"opencode\"}}}}\n"
        );
        let output = serve(
            &input,
            Some(temp.path().join("kl.toml")),
            temp.path().join(".kl"),
        );
        let providers = output.iter().find(|value| value["id"] == 2).unwrap();
        assert_eq!(providers["result"]["models"].as_array().unwrap().len(), 0);
        let create = output.iter().find(|value| value["id"] == 3).unwrap();
        assert_eq!(create["error"]["code"], error_codes::PROVIDER_UNAVAILABLE);
        assert!(
            create["error"]["message"]
                .as_str()
                .unwrap()
                .contains("disabled")
        );
    }

    #[test]
    fn provider_upsert_and_key_survive_core_restart() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("kl.toml");
        let home_path = temp.path().join("home");
        let input = r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"provider.upsert","params":{"id":"local","protocols":{"openai-responses":"https://gateway.example/custom/responses?tenant=kl"},"models":[{"id":"gpt-5-responses","protocol":"openai-responses","context_window":1000,"compact_threshold":0.8,"max_output_tokens":100}],"enabled":true}}
{"jsonrpc":"2.0","id":3,"method":"provider.set_api_key","params":{"id":"local","key":"super-secret"}}
{"jsonrpc":"2.0","id":4,"method":"config.set_default_model","params":{"provider":"local","model":"gpt-5-responses"}}
"#;
        let first = serve(input, Some(config_path.clone()), home_path.clone());
        assert!(
            first.iter().all(|value| value.get("error").is_none()),
            "{first:?}"
        );

        let second = serve(
            r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"provider.list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"config.get","params":{}}
"#,
            Some(config_path),
            home_path.clone(),
        );
        let providers = second.iter().find(|value| value["id"] == 2).unwrap();
        let models = providers["result"]["models"].as_array().unwrap();
        assert_eq!(models.len(), 1);
        assert_eq!(models[0]["provider"], "local");
        assert_eq!(models[0]["model"], "gpt-5-responses");
        assert_eq!(models[0]["protocol"], "openai-responses");
        assert_eq!(models[0]["enabled"], true);
        assert_eq!(models[0]["key_configured"], true);
        assert_eq!(
            models[0]["url"],
            "https://gateway.example/custom/responses?tenant=kl"
        );
        let config = second.iter().find(|value| value["id"] == 3).unwrap();
        assert!(!config.to_string().contains("super-secret"));
        assert_eq!(
            std::fs::read_to_string(home_path.join("credentials/local.key")).unwrap(),
            "super-secret"
        );
        assert!(
            !std::fs::read_to_string(temp.path().join("providers/local.toml"))
                .unwrap()
                .contains("super-secret")
        );
    }
    #[test]
    fn creates_scripted_session_and_lists_it() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("script.json"), r#"[{"text":"hello"}]"#).unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
"#,
        )
        .unwrap();
        let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
        let input = format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\"}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.list\",\"params\":{{}}}}\n"
        );
        let output = serve(
            &input,
            Some(temp.path().join("kl.toml")),
            temp.path().join(".kl"),
        );
        let create = output
            .iter()
            .find(|value| value["id"] == 2)
            .expect("session.create response");
        let list = output
            .iter()
            .find(|value| value["id"] == 3)
            .expect("session.list response");
        assert_eq!(create["result"]["provider"], "scripted", "{create}");
        assert!(create["result"]["session_id"].as_str().is_some());
        assert_eq!(list["result"]["sessions"].as_array().unwrap().len(), 1);
    }
}
