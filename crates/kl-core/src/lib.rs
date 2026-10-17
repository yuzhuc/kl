//! KL core: the only trusted execution engine.
//!
//! The process boundary is JSON-RPC 2.0 over newline-delimited stdio. Turn
//! work runs on a thread so the request loop can keep serving cancellation and
//! approval responses.

mod config;

pub use config::{Config, ConfigError};

use kl_agent::{Agent, AgentConfig, AgentError, ApprovalGate, ApprovalRequest, EventSink};
use kl_audit::JsonlAudit;
use kl_policy::{PolicyEngine, PolicyError};
use kl_protocol::{
    ApprovalDecision, ApprovalRespondParams, AuditTailParams, CORE_NAME, CORE_VERSION, CoreEvent,
    DoctorCheck, DoctorResult, EventNotification, HandshakeParams, HandshakeResult,
    IncomingMessage, MIN_PROTOCOL_VERSION, PROTOCOL_VERSION, PingParams, PingResult,
    ProviderListResult, ProviderSummary, RequestId, RpcError, SessionCreateParams, SessionInfo,
    SessionListResult, SessionResumeParams, TurnCancelParams, TurnCancelResult, TurnStartParams,
    TurnStartResult, error_codes, failure, failure_with_data, methods, negotiate, success,
    validate_envelope,
};
use kl_provider::{
    AnthropicProvider, ChatMessage, OpenAiChatProvider, Provider, ProviderRouter, ScriptedProvider,
};
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
];

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
    router: ProviderRouter,
    tools: Arc<ToolRegistry>,
    store: Arc<SessionStore>,
    audit: Arc<JsonlAudit>,
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
        let config_dir = Config::config_dir(&context.config_path);
        let providers = build_providers(&config, &config_dir);
        let router = ProviderRouter::new(
            providers,
            config.agent.provider.clone(),
            config.agent.model.clone(),
        );
        let store = Arc::new(SessionStore::new(context.home.join("sessions"))?);
        let audit = Arc::new(JsonlAudit::open(
            context.home.join("audit").join("events.jsonl"),
        )?);
        let hub = EventHub::new(emitter, store.clone());
        let approval_timeout = Duration::from_secs(config.agent.approval_timeout_secs.max(1));
        Ok(Self {
            context,
            config,
            router,
            tools: Arc::new(ToolRegistry::builtin()),
            store,
            audit,
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
            methods::AUDIT_TAIL => self.audit_tail(params),
            methods::CONFIG_GET => self.config_get(),
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
        let provider_check = match self.router.get(Some(&self.config.agent.provider)) {
            Ok(provider) => {
                let (available, detail) = provider.available();
                DoctorCheck {
                    name: "provider_active".to_owned(),
                    ok: available,
                    detail: format!("{} ({detail})", provider.id()),
                }
            }
            Err(error) => DoctorCheck {
                name: "provider_active".to_owned(),
                ok: false,
                detail: error.to_string(),
            },
        };
        checks.push(provider_check);
        checks.push(DoctorCheck {
            name: "tools".to_owned(),
            ok: true,
            detail: self.tools.names().join(", "),
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
        let provider = self
            .router
            .get(Some(&provider_id))
            .map_err(provider_error)?;
        check_provider_available(provider.as_ref())?;
        let model = self.select_model(provider.as_ref(), &provider_id, params.model.as_deref());

        let data = self
            .store
            .create(&workspace.to_string_lossy(), &provider_id, &model)
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

        let (provider_id, model, workspace, history, provider) = {
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
            let provider = self
                .router
                .get(Some(&provider_id))
                .map_err(provider_error)?;
            check_provider_available(provider.as_ref())?;
            let model = match params.model.clone() {
                Some(model) => model,
                None if provider_id == state.meta.provider => state.meta.model.clone(),
                None => self.select_model(provider.as_ref(), &provider_id, None),
            };
            let workspace = state.meta.workspace.clone();
            state.active_turn = Some(turn_id.clone());
            state.meta.provider = provider_id.clone();
            state.meta.model = model.clone();
            (
                provider_id,
                model,
                workspace,
                state.messages.clone(),
                provider,
            )
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
        let thread_provider_id = provider_id.clone();
        let thread_model = model.clone();
        let thread_cancel = cancel.clone();
        let handle = thread::spawn(move || {
            let agent = Agent::new(
                provider,
                thread_model.clone(),
                tools,
                policy,
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
                &thread_cancel,
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
                    state.meta.provider = thread_provider_id;
                    state.meta.model = thread_model;
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
            provider: provider_id,
            model,
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
        let providers = self
            .router
            .list()
            .into_iter()
            .map(|provider| ProviderSummary {
                active: provider.id == self.config.agent.provider,
                id: provider.id,
                kind: provider.kind,
                models: provider.models,
                available: provider.available,
                detail: provider.detail,
            })
            .collect();
        to_value(ProviderListResult {
            active_provider: self.config.agent.provider.clone(),
            providers,
        })
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

    fn config_get(&self) -> Result<Value, RpcError> {
        to_value(json!({
            "path": self.context.config_path,
            "config": self.config.redacted(),
        }))
    }

    fn select_model(
        &self,
        provider: &dyn Provider,
        provider_id: &str,
        requested: Option<&str>,
    ) -> String {
        if let Some(model) = requested.filter(|model| !model.is_empty()) {
            return model.to_owned();
        }
        if provider_id == self.config.agent.provider && !self.config.agent.model.is_empty() {
            return self.config.agent.model.clone();
        }
        provider
            .models()
            .first()
            .map(|model| model.id.clone())
            .unwrap_or_else(|| self.config.agent.model.clone())
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

fn build_providers(config: &Config, config_dir: &Path) -> Vec<Arc<dyn Provider>> {
    config
        .providers
        .iter()
        .map(|provider| {
            let models = if provider.models.is_empty() {
                default_models(&provider.kind)
            } else {
                provider.models.clone()
            };
            let key = if provider.api_key_env.is_empty() {
                None
            } else {
                std::env::var(&provider.api_key_env)
                    .ok()
                    .filter(|value| !value.trim().is_empty())
            };
            let base_url = if provider.base_url.is_empty() {
                default_base_url(&provider.kind)
            } else {
                provider.base_url.clone()
            };
            let instance: Arc<dyn Provider> = match provider.kind.as_str() {
                "openai-chat" => Arc::new(OpenAiChatProvider::new(
                    provider.id.clone(),
                    base_url,
                    key,
                    models,
                )),
                "anthropic" => Arc::new(AnthropicProvider::new(
                    provider.id.clone(),
                    base_url,
                    key,
                    models,
                )),
                "scripted" => {
                    let script = provider.script_path.as_deref().map(|raw| {
                        let path = Path::new(raw);
                        if path.is_absolute() {
                            path.to_path_buf()
                        } else {
                            config_dir.join(path)
                        }
                    });
                    match script {
                        Some(path) => Arc::new(
                            ScriptedProvider::from_file(provider.id.clone(), path)
                                .unwrap_or_else(|_| ScriptedProvider::empty(provider.id.clone())),
                        ),
                        None => Arc::new(ScriptedProvider::empty(provider.id.clone())),
                    }
                }
                _ => unreachable!("provider kind validated by Config::validate"),
            };
            instance
        })
        .collect()
}

fn default_models(kind: &str) -> Vec<String> {
    match kind {
        "openai-chat" => vec!["gpt-4o-mini".to_owned()],
        "anthropic" => vec!["claude-sonnet-4-5".to_owned()],
        _ => vec!["scripted".to_owned()],
    }
}

fn default_base_url(kind: &str) -> String {
    match kind {
        "openai-chat" => "https://api.openai.com/v1".to_owned(),
        "anthropic" => "https://api.anthropic.com".to_owned(),
        _ => String::new(),
    }
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
    fn creates_scripted_session_and_lists_it() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("script.json"), r#"[{"text":"hello"}]"#).unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "scripted"
model = "scripted"

[[providers]]
id = "scripted"
kind = "scripted"
models = ["scripted"]
script_path = "script.json"
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
