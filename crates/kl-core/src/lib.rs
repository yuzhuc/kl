//! KL core: the only trusted execution engine.
//!
//! The process boundary is JSON-RPC 2.0 over newline-delimited stdio. Turn
//! work runs on a thread so the request loop can keep serving cancellation and
//! approval responses.

mod agents;
mod config;
mod hooks;
pub mod mcp;
mod skills;
mod subagent;

pub use agents::AgentProfile;
pub use config::{
    AgentPreset, Config, ConfigError, ProviderCatalog, QualityMode, RoleSettings,
    config_set_default_model, provider_set_api_key, provider_set_enabled, provider_upsert,
};

use config::AgentSettings;
use kl_agent::{
    Agent, AgentConfig, AgentError, ApprovalGate, ApprovalRequest, EventSink, TurnResult,
    TurnStatus,
};
use kl_audit::{AuditLog, AuditRecord, AuditSink};
use kl_plugin_host::{
    PluginError, PluginHost, PluginInfo, PluginKind, SidecarProvider, SidecarTool,
};
use kl_policy::{PolicyEngine, PolicyError, ToolAccess};
use kl_protocol::{
    AgentConfigResult, AgentConfigSetParams, AgentInfo, AgentListResult, ApprovalDecision,
    ApprovalMode, ApprovalRespondParams, AuditTailParams, CORE_NAME, CORE_VERSION, CoreEvent,
    DefaultModelResult, DoctorCheck, DoctorResult, EventNotification, HandshakeParams,
    HandshakeResult, IncomingMessage, MIN_PROTOCOL_VERSION, McpListResult, McpToolInfo,
    PROTOCOL_VERSION, PermissionsResult, PermissionsSetParams, PingParams, PingResult,
    PluginIdParams, PluginInstallParams, PluginListResult, ProviderEnabledResult,
    ProviderKeyResult, ProviderListResult, ProviderModelSummary, ProviderSetApiKeyParams,
    ProviderSetEnabledParams, ProviderUpsertParams, ProviderUpsertResult, RequestId, ReviewFinding,
    ReviewResult, ReviewRunParams, RpcError, SandboxCapabilitiesSummary, SandboxSummary,
    SessionCompactParams, SessionCompactResult, SessionCreateParams, SessionDeleteParams,
    SessionDeleteResult, SessionHistoryEntry, SessionHistoryParams, SessionHistoryResult,
    SessionHistoryToolSummary, SessionInfo, SessionListResult, SessionRenameParams,
    SessionResumeParams, SessionStatusParams, SessionStatusResult, SetDefaultModelParams,
    SkillGetParams, SkillGetResult, SkillInfo, SkillListParams, SkillListResult, TurnCancelParams,
    TurnCancelResult, TurnStartParams, TurnStartResult, WorkspaceInitParams, WorkspaceInitResult,
    error_codes, failure, failure_with_data, methods, negotiate, success, validate_envelope,
};
use kl_provider::{
    AnthropicAdapter, BoundProvider, CancellationToken, ChatEvent, ChatMessage, ChatOutcome,
    ChatRequest, ModelMetadata, OpenAiChatAdapter, OpenAiResponsesAdapter, Protocol,
    ProtocolAdapter, Provider, ProviderError, ProviderRouter, ScriptedProvider, Usage,
};
use kl_review::{
    Finding, ReviewContext, ReviewError, ReviewMode, ReviewReport, collect_context, finalize,
    llm_warning, model_prompt, parse_model_findings, runtime_findings, scan_local,
};
use kl_sandbox::{SandboxManager, SandboxPolicy};
use kl_store::{
    SessionData, SessionMeta, SessionStore, StoreError, TURN_STATE_IDLE,
    TURN_STATE_WAITING_APPROVAL, TURN_STATE_WORKING,
};
use kl_tools::{ToolError, ToolRegistry, ToolSource};
use serde::Serialize;
use serde::de::DeserializeOwned;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::collections::hash_map::DefaultHasher;
use std::hash::{Hash, Hasher};
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub const FEATURES: &[&str] = &[
    "events",
    "sessions",
    "history",
    "tools",
    "audit",
    "approvals",
    "providers",
    "plugins",
    "review",
    "quality-modes",
];

type ProviderRoutes = Vec<(ModelMetadata, Arc<dyn Provider>)>;
type ResolvedStage = (Arc<dyn Provider>, ModelMetadata, Option<String>);

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
    #[error("hook error: {0}")]
    Hook(String),
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

    fn register(&self, approval_id: &str) -> mpsc::Receiver<ApprovalDecision> {
        let (sender, receiver) = mpsc::channel();
        self.pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(approval_id.to_owned(), sender);
        receiver
    }

    fn wait_registered(
        &self,
        approval_id: &str,
        receiver: mpsc::Receiver<ApprovalDecision>,
        cancel: &AtomicBool,
    ) -> ApprovalDecision {
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
    sessions: Arc<Mutex<HashMap<String, SessionState>>>,
    session_id: String,
    cancel: Arc<AtomicBool>,
}

impl CoreApprovalGate {
    fn rule_key(request: &ApprovalRequest) -> String {
        format!("{}:{}", request.tool, request.resource)
    }

    fn is_remembered(&self, key: &str) -> bool {
        self.sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .get(&self.session_id)
            .is_some_and(|state| state.approval_rules.iter().any(|rule| rule == key))
    }

    fn remember(&self, key: String) {
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let Some(state) = sessions.get_mut(&self.session_id) else {
            return;
        };
        if state.approval_rules.iter().any(|rule| rule == &key) {
            return;
        }
        state.approval_rules.push(key);
        let data = SessionData {
            meta: state.meta.clone(),
            messages: state
                .messages
                .iter()
                .map(|message| serde_json::to_value(message).unwrap_or(Value::Null))
                .collect(),
            approval_rules: state.approval_rules.clone(),
        };
        let _ = self.hub.store.save(&data);
    }
}

impl ApprovalGate for CoreApprovalGate {
    fn request(&self, request: ApprovalRequest) -> ApprovalDecision {
        let key = Self::rule_key(&request);
        if self.is_remembered(&key) {
            let _ = self.hub.emit(
                &self.session_id,
                CoreEvent::ToolApprovalResolved {
                    turn_id: request.turn_id,
                    call_id: request.call_id,
                    approval_id: request.approval_id,
                    decision: ApprovalDecision::Allow,
                },
            );
            return ApprovalDecision::Allow;
        }
        let _ = set_turn_state(
            &self.sessions,
            &self.hub.store,
            &self.session_id,
            TURN_STATE_WAITING_APPROVAL,
        );
        let receiver = self.broker.register(&request.approval_id);
        let _ = self.hub.emit(
            &self.session_id,
            CoreEvent::ToolApprovalRequested {
                turn_id: request.turn_id.clone(),
                call_id: request.call_id.clone(),
                approval_id: request.approval_id.clone(),
                name: request.tool.clone(),
                action: request.action.clone(),
                resource: request.resource.clone(),
                args: request.args.clone(),
                reason: request.reason.clone(),
            },
        );
        let decision = self
            .broker
            .wait_registered(&request.approval_id, receiver, &self.cancel);
        let _ = set_turn_state(
            &self.sessions,
            &self.hub.store,
            &self.session_id,
            TURN_STATE_WORKING,
        );
        let effective = if decision == ApprovalDecision::AllowSession {
            self.remember(key);
            ApprovalDecision::Allow
        } else {
            decision
        };
        let _ = self.hub.emit(
            &self.session_id,
            CoreEvent::ToolApprovalResolved {
                turn_id: request.turn_id,
                call_id: request.call_id,
                approval_id: request.approval_id,
                decision: effective,
            },
        );
        effective
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DeepStage {
    Explorer,
    Planner,
    Implementer,
    Reviewer,
    Verifier,
}

impl DeepStage {
    fn name(self) -> &'static str {
        match self {
            Self::Explorer => "explorer",
            Self::Planner => "planner",
            Self::Implementer => "implementer",
            Self::Reviewer => "reviewer",
            Self::Verifier => "verifier",
        }
    }

    fn prompt(self, task: &str, index_context: Option<&str>) -> String {
        match self {
            Self::Explorer => {
                let prompt = format!(
                    "You are the explorer stage. Inspect the workspace and session history. Use read_file, grep, glob and git diff to find the files, symbols, tests and risks relevant to this task. Do not modify files. End with a concise exploration report.\n\nTask:\n{task}"
                );
                with_index_context(prompt, index_context)
            }
            Self::Planner => {
                let prompt = format!(
                    "You are the planner stage. Use the explorer report and session history to produce an ordered implementation plan with explicit acceptance checks. Do not modify files. End with PLAN and the verification commands.\n\nTask:\n{task}"
                );
                with_index_context(prompt, index_context)
            }
            Self::Implementer => format!(
                "You are the implementer stage. Implement the plan using the available tools. Make the smallest correct change, run focused verification, and do not expand scope. End with IMPLEMENTATION and the commands/results you ran.\n\nTask:\n{task}"
            ),
            Self::Reviewer => format!(
                "You are the reviewer stage. Inspect the actual git diff and relevant files. Do not modify files. Report every finding with file:line, severity, confidence, evidence, and a concrete fix. If there are no findings, say so explicitly. End with REVIEW.\n\nTask:\n{task}"
            ),
            Self::Verifier => format!(
                "You are the verifier stage. Verify the implementation by running the relevant tests, build, and type/lint checks. Do not modify files or expand the change. Report commands, results, and residual risks with file:line evidence. End with VERIFY.\n\nTask:\n{task}"
            ),
        }
    }
}

fn with_index_context(prompt: String, index_context: Option<&str>) -> String {
    match index_context {
        Some(context) => format!(
            "{prompt}\n\nRepository index (hints only; verify with tools before relying on them):\n{context}"
        ),
        None => prompt,
    }
}

const DEEP_STAGES: [DeepStage; 5] = [
    DeepStage::Explorer,
    DeepStage::Planner,
    DeepStage::Implementer,
    DeepStage::Reviewer,
    DeepStage::Verifier,
];

struct StageSink {
    hub: EventHub,
    session_id: String,
    stage: &'static str,
    stage_turn: String,
    base_turn: String,
    cancel: Arc<AtomicBool>,
    tool_calls: Arc<AtomicUsize>,
    max_tool_calls: usize,
    budget_exceeded: Arc<AtomicBool>,
}

impl EventSink for StageSink {
    fn emit(&self, event: CoreEvent) -> Result<(), AgentError> {
        if matches!(&event, CoreEvent::ToolStarted { .. }) {
            let count = self.tool_calls.fetch_add(1, Ordering::Relaxed) + 1;
            if count >= self.max_tool_calls {
                self.budget_exceeded.store(true, Ordering::Relaxed);
                self.cancel.store(true, Ordering::Relaxed);
            }
        }
        let forwarded = match event {
            CoreEvent::TurnStarted { .. } => CoreEvent::TurnStarted {
                turn_id: self.stage_turn.clone(),
            },
            CoreEvent::TurnCompleted { status, text, .. } => {
                if status == "cancelled" {
                    return Ok(());
                }
                CoreEvent::TurnDelta {
                    turn_id: self.base_turn.clone(),
                    text: format!("\n[{}] {}\n", self.stage, text.trim()),
                }
            }
            CoreEvent::TurnFailed { message, .. } => CoreEvent::TurnDelta {
                turn_id: self.base_turn.clone(),
                text: format!("\n[{}] failed: {}\n", self.stage, message.trim()),
            },
            other => other,
        };
        self.hub
            .emit(&self.session_id, forwarded)
            .map(|_| ())
            .map_err(AgentError::Event)
    }
}

struct Watchdog {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl Drop for Watchdog {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(handle) = self.handle.take() {
            let _ = handle.join();
        }
    }
}

fn deep_watchdog(
    cancel: Arc<AtomicBool>,
    timed_out: Arc<AtomicBool>,
    timeout: Duration,
) -> Watchdog {
    let stop = Arc::new(AtomicBool::new(false));
    let thread_stop = stop.clone();
    let handle = thread::spawn(move || {
        let deadline = Instant::now() + timeout;
        loop {
            if thread_stop.load(Ordering::Relaxed) || cancel.load(Ordering::Relaxed) {
                return;
            }
            if Instant::now() >= deadline {
                timed_out.store(true, Ordering::Relaxed);
                cancel.store(true, Ordering::Relaxed);
                return;
            }
            thread::sleep(Duration::from_millis(50));
        }
    });
    Watchdog {
        stop,
        handle: Some(handle),
    }
}

struct DeepTurn {
    router: ProviderRouter,
    settings: AgentSettings,
    tools: Arc<ToolRegistry>,
    policy: Arc<PolicyEngine>,
    sandbox: Arc<SandboxManager>,
    audit: Arc<dyn kl_audit::AuditSink>,
    sink: SessionSink,
    gate: CoreApprovalGate,
    cancel: Arc<AtomicBool>,
    session_id: String,
    base_turn_id: String,
    default_provider: String,
    default_model: String,
    task: String,
    workspace: PathBuf,
    tool_access: ToolAccess,
}

impl DeepTurn {
    fn run(self, history: &mut Vec<ChatMessage>) -> Result<TurnResult, AgentError> {
        let timed_out = Arc::new(AtomicBool::new(false));
        let _watchdog = deep_watchdog(
            self.cancel.clone(),
            timed_out.clone(),
            Duration::from_secs(self.settings.deep_timeout_secs.max(1)),
        );
        let tool_calls = Arc::new(AtomicUsize::new(0));
        let budget_exceeded = Arc::new(AtomicBool::new(false));
        let max_tools = self.settings.deep_max_tool_calls.max(1);
        let token_budget = self.settings.deep_token_budget;
        let mut usage = Usage::default();
        let mut outputs = Vec::new();
        let mut stable_workspace = None;
        let max_stages = self.settings.deep_max_stages.clamp(1, DEEP_STAGES.len());
        let index = match kl_index::RepoIndex::load(&self.workspace) {
            Ok(index) => Some(index),
            Err(error) => {
                self.emit_delta(&format!("[index] unavailable: {error}\n"))?;
                None
            }
        };
        let index_context = index.as_ref().map(|index| {
            let changed = index.changed_files().unwrap_or_default();
            index.prompt_context(&self.task, &changed)
        });
        for stage in DEEP_STAGES.iter().take(max_stages).copied() {
            if self.cancel.load(Ordering::Relaxed) {
                return self.stop_or_cancel(&timed_out, &budget_exceeded, usage);
            }
            let used = usage.input_tokens + usage.output_tokens;
            if token_budget > 0 && used >= token_budget {
                return Err(AgentError::TokenBudgetExceeded);
            }
            let remaining_tools = max_tools.saturating_sub(tool_calls.load(Ordering::Relaxed));
            if remaining_tools == 0 {
                return Err(AgentError::Tool("deep tool budget exceeded".to_owned()));
            }
            if stage == DeepStage::Reviewer {
                stable_workspace = Some(workspace_fingerprint(&self.workspace));
            }
            let (provider, metadata, warning) = resolve_stage_provider(
                &self.router,
                &self.settings,
                stage,
                &self.default_provider,
                &self.default_model,
            )?;
            if let Some(warning) = warning {
                self.emit_delta(&format!("[{}] {warning}\n", stage.name()))?;
            }
            let stage_turn = format!("{}:{}", self.base_turn_id, stage.name());
            let stage_sink = StageSink {
                hub: self.sink.hub.clone(),
                session_id: self.session_id.clone(),
                stage: stage.name(),
                stage_turn: stage_turn.clone(),
                base_turn: self.base_turn_id.clone(),
                cancel: self.cancel.clone(),
                tool_calls: tool_calls.clone(),
                max_tool_calls: max_tools,
                budget_exceeded: budget_exceeded.clone(),
            };
            let agent_config = AgentConfig {
                max_iterations: self.settings.max_iterations.max(1),
                max_tool_calls_per_turn: remaining_tools.clamp(1, 32),
                max_tool_output_bytes: self.settings.max_tool_output_bytes,
                command_timeout: Duration::from_secs(self.settings.command_timeout_secs.max(1)),
                max_total_tokens: if token_budget == 0 {
                    0
                } else {
                    token_budget.saturating_sub(used)
                },
                tool_access: match self.tool_access {
                    ToolAccess::Full => match stage {
                        DeepStage::Reviewer => ToolAccess::ReadOnly,
                        DeepStage::Verifier => ToolAccess::VerifyOnly,
                        _ => ToolAccess::Full,
                    },
                    access => access,
                },
            };
            let agent = Agent::new(
                provider,
                metadata,
                self.tools.clone(),
                self.policy.clone(),
                self.sandbox.clone(),
                self.audit.clone(),
                agent_config,
            );
            let prompt = stage.prompt(&self.task, index_context.as_deref());
            let result = agent.run_turn(
                &self.session_id,
                &stage_turn,
                &prompt,
                history,
                self.cancel.clone(),
                &stage_sink,
                &self.gate,
            );
            match result {
                Ok(result) => {
                    usage.input_tokens += result.usage.input_tokens;
                    usage.output_tokens += result.usage.output_tokens;
                    if result.status == TurnStatus::Cancelled {
                        return self.stop_or_cancel(&timed_out, &budget_exceeded, usage);
                    }
                    outputs.push(format!("[{}] {}", stage.name(), result.text.trim()));
                }
                Err(error) => {
                    if self.cancel.load(Ordering::Relaxed) {
                        return self.stop_or_cancel(&timed_out, &budget_exceeded, usage);
                    }
                    if matches!(error, AgentError::TokenBudgetExceeded) {
                        return Err(error);
                    }
                    outputs.push(format!("[{}] failed: {error}", stage.name()));
                    if stage == DeepStage::Implementer {
                        return Err(error);
                    }
                }
            }
            if matches!(stage, DeepStage::Reviewer | DeepStage::Verifier) {
                if let Some(expected) = &stable_workspace {
                    if workspace_fingerprint(&self.workspace) != *expected {
                        self.emit_delta(
                            "[review/verify] workspace changed; refusing to continue\n",
                        )?;
                        return Err(AgentError::Tool(
                            "reviewer or verifier modified the workspace".to_owned(),
                        ));
                    }
                }
            }
        }
        let completed: Vec<&str> = DEEP_STAGES
            .iter()
            .take(max_stages)
            .map(|stage| stage.name())
            .collect();
        let text = format!(
            "deep mode completed: {}\n\n{}",
            completed.join(" -> "),
            outputs.join("\n\n")
        );
        self.sink.emit(CoreEvent::TurnCompleted {
            turn_id: self.base_turn_id.clone(),
            status: TurnStatus::Completed.as_str().to_owned(),
            text: text.clone(),
            input_tokens: usage.input_tokens,
            output_tokens: usage.output_tokens,
            cost_usd: None,
        })?;
        Ok(TurnResult {
            status: TurnStatus::Completed,
            text,
            usage,
        })
    }

    fn stop_or_cancel(
        self,
        timed_out: &AtomicBool,
        budget_exceeded: &AtomicBool,
        usage: Usage,
    ) -> Result<TurnResult, AgentError> {
        if timed_out.load(Ordering::Relaxed) {
            return Err(AgentError::Tool("deep time budget exceeded".to_owned()));
        }
        if budget_exceeded.load(Ordering::Relaxed) {
            return Err(AgentError::Tool("deep tool budget exceeded".to_owned()));
        }
        let text = "Cancelled by user.".to_owned();
        self.sink.emit(CoreEvent::TurnCompleted {
            turn_id: self.base_turn_id.clone(),
            status: TurnStatus::Cancelled.as_str().to_owned(),
            text: text.clone(),
            input_tokens: usage.input_tokens,
            output_tokens: usage.output_tokens,
            cost_usd: None,
        })?;
        Ok(TurnResult {
            status: TurnStatus::Cancelled,
            text,
            usage,
        })
    }

    fn emit_delta(&self, text: &str) -> Result<(), AgentError> {
        self.sink.emit(CoreEvent::TurnDelta {
            turn_id: self.base_turn_id.clone(),
            text: text.to_owned(),
        })
    }
}

fn workspace_fingerprint(workspace: &Path) -> String {
    let mut hasher = DefaultHasher::new();
    let is_git = std::process::Command::new("git")
        .arg("-C")
        .arg(workspace)
        .args(["rev-parse", "--is-inside-work-tree"])
        .output()
        .map(|output| output.status.success())
        .unwrap_or(false);
    if is_git {
        if let Ok(output) = std::process::Command::new("git")
            .arg("-C")
            .arg(workspace)
            .args(["diff", "--no-ext-diff"])
            .output()
        {
            output.stdout.hash(&mut hasher);
        }
        if let Ok(output) = std::process::Command::new("git")
            .arg("-C")
            .arg(workspace)
            .args(["status", "--porcelain"])
            .output()
        {
            output.stdout.hash(&mut hasher);
        }
        if let Ok(output) = std::process::Command::new("git")
            .arg("-C")
            .arg(workspace)
            .args(["ls-files", "--others", "--exclude-standard"])
            .output()
        {
            for path in String::from_utf8_lossy(&output.stdout)
                .lines()
                .filter(|line| !line.trim().is_empty())
            {
                path.hash(&mut hasher);
                if let Ok(bytes) = std::fs::read(workspace.join(path)) {
                    bytes.hash(&mut hasher);
                }
            }
        }
    } else {
        hash_tree(workspace, &mut hasher);
    }
    format!("{:x}", hasher.finish())
}

fn hash_tree(directory: &Path, hasher: &mut DefaultHasher) {
    let Ok(entries) = std::fs::read_dir(directory) else {
        return;
    };
    let mut entries: Vec<_> = entries.flatten().collect();
    entries.sort_by_key(|entry| entry.path());
    for entry in entries {
        let name = entry.file_name();
        if matches!(
            name.to_str(),
            Some(".git" | "target" | "bin" | "obj" | ".kl" | "node_modules")
        ) {
            continue;
        }
        let path = entry.path();
        if entry.file_type().map(|kind| kind.is_dir()).unwrap_or(false) {
            hash_tree(&path, hasher);
        } else if let Ok(bytes) = std::fs::read(&path) {
            path.hash(hasher);
            bytes.hash(hasher);
        }
    }
}
fn resolve_stage_provider(
    router: &ProviderRouter,
    settings: &AgentSettings,
    stage: DeepStage,
    default_provider: &str,
    default_model: &str,
) -> Result<ResolvedStage, AgentError> {
    let mut warning = None;
    if let Some(role) = settings.roles.get(stage.name()) {
        let provider_id = role.provider.as_deref().unwrap_or(default_provider);
        let model = role
            .model
            .as_deref()
            .or_else(|| (provider_id == default_provider).then_some(default_model));
        match router.get(Some(provider_id), model) {
            Ok((provider, metadata)) => {
                let (available, detail) = provider.available();
                if available {
                    return Ok((provider, metadata, None));
                }
                warning = Some(format!(
                    "role provider `{}` unavailable ({}); falling back to {default_provider}/{default_model}",
                    provider.id(),
                    detail
                ));
            }
            Err(error) => {
                warning = Some(format!(
                    "role override rejected ({}); falling back to {default_provider}/{default_model}",
                    error
                ));
            }
        }
    }
    let (provider, metadata) = router
        .get(Some(default_provider), Some(default_model))
        .map_err(AgentError::Provider)?;
    let (available, detail) = provider.available();
    if !available {
        return Err(AgentError::Provider(ProviderError::config(format!(
            "provider `{}` unavailable: {detail}",
            provider.id()
        ))));
    }
    Ok((provider, metadata, warning))
}
struct SessionState {
    meta: SessionMeta,
    messages: Vec<ChatMessage>,
    active_turn: Option<String>,
    approval_rules: Vec<String>,
}

fn set_turn_state(
    sessions: &Arc<Mutex<HashMap<String, SessionState>>>,
    store: &SessionStore,
    session_id: &str,
    turn_state: &str,
) -> Result<(), StoreError> {
    let meta = store.set_turn_state(session_id, turn_state)?;
    let mut sessions = sessions.lock().unwrap_or_else(|error| error.into_inner());
    if let Some(state) = sessions.get_mut(session_id) {
        state.meta.turn_state = meta.turn_state;
        state.meta.updated_at = meta.updated_at;
    }
    Ok(())
}

struct Core {
    context: ServerContext,
    config: Config,
    agent_profiles: Vec<AgentProfile>,
    catalog: ProviderCatalog,
    router: ProviderRouter,
    tools: Arc<ToolRegistry>,
    store: Arc<SessionStore>,
    audit: Arc<AuditLog>,
    sandbox: Arc<SandboxManager>,
    plugin_host: Arc<PluginHost>,
    hub: EventHub,
    sessions: Arc<Mutex<HashMap<String, SessionState>>>,
    current_session: Option<String>,
    approvals: Arc<ApprovalBroker>,
    cancels: Arc<Mutex<HashMap<String, Arc<AtomicBool>>>>,
    threads: Vec<JoinHandle<()>>,
    handshaken: bool,
}

impl Core {
    fn new(context: ServerContext, emitter: Emitter) -> Result<Self, CoreError> {
        std::fs::create_dir_all(&context.home)?;
        let config = Config::load(&context.config_path)?;
        let agent_profiles = agents::load(&context.home);
        let hook_config =
            hooks::HookConfig::load(&context.home.join("hooks.toml")).map_err(CoreError::Hook)?;
        let sandbox = Arc::new(SandboxManager::probe(&config.sandbox));
        let plugin_host = Arc::new(PluginHost::with_sandbox(
            context.home.join("plugins"),
            sandbox.clone(),
        )?);
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
        let store = Arc::new(SessionStore::new(context.home.join("sessions"))?);
        let audit = Arc::new(AuditLog::open(
            context.home.join("audit"),
            config.audit.rotate_bytes,
            config.audit.keep_files,
        )?);
        let tools = Arc::new(ToolRegistry::builtin());
        tools.register(Arc::new(skills::SkillTool {
            home: context.home.clone(),
        }));
        register_plugin_adapters(&plugin_host, &router, &tools);
        // shortcut: MCP startup handshake is capped at 10s; tool calls still use the agent timeout.
        register_mcp_tools(
            &context.home,
            &tools,
            Duration::from_secs(config.agent.command_timeout_secs.clamp(1, 10)),
            &sandbox,
            &mcp_workspace(&context),
        );
        tools.register(Arc::new(subagent::SubagentTool::new(
            router.clone(),
            tools.clone(),
            sandbox.clone(),
            audit.clone(),
            AgentConfig {
                max_iterations: config.agent.max_iterations.max(1),
                max_tool_calls_per_turn: 32,
                max_tool_output_bytes: config.agent.max_tool_output_bytes,
                command_timeout: Duration::from_secs(config.agent.command_timeout_secs.max(1)),
                max_total_tokens: 0,
                tool_access: ToolAccess::ReadOnly,
            },
        )));
        if !hook_config.is_empty() {
            let runner = Arc::new(hooks::HookRunner::new(hook_config));
            for name in tools.names() {
                if let Some(tool) = tools.get(&name) {
                    tools.register(Arc::new(hooks::HookedTool::new(tool, runner.clone())));
                }
            }
        }
        let hub = EventHub::new(emitter, store.clone());
        let approval_timeout = Duration::from_secs(config.agent.approval_timeout_secs.max(1));
        Ok(Self {
            context,
            config,
            agent_profiles,
            catalog,
            router,
            tools,
            store,
            audit,
            sandbox,
            plugin_host,
            hub,
            sessions: Arc::new(Mutex::new(HashMap::new())),
            current_session: None,
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
            methods::SESSION_HISTORY => self.session_history(params),
            methods::SESSION_LIST => self.session_list(),
            methods::SESSION_RENAME => self.session_rename(params),
            methods::SESSION_DELETE => self.session_delete(params),
            methods::SESSION_STATUS => self.session_status(params),
            methods::SESSION_COMPACT => self.session_compact(params),
            methods::SESSION_FORK => self.session_fork(params),
            methods::TURN_START => self.turn_start(params),
            methods::TURN_CANCEL => self.turn_cancel(params),
            methods::APPROVAL_RESPOND => self.approval_respond(params),
            methods::PERMISSIONS_GET => self.permissions_get(),
            methods::PERMISSIONS_SET => self.permissions_set(params),
            methods::PROVIDER_LIST => self.provider_list(),
            methods::PROVIDER_UPSERT => self.provider_upsert(params),
            methods::PROVIDER_SET_API_KEY => self.provider_set_api_key(params),
            methods::PROVIDER_SET_ENABLED => self.provider_set_enabled(params),
            methods::CONFIG_SET_DEFAULT_MODEL => self.config_set_default_model(params),
            methods::AUDIT_TAIL => self.audit_tail(params),
            methods::REVIEW_RUN => self.review_run(params),
            methods::CONFIG_GET => self.config_get(),
            methods::WORKSPACE_INIT => self.workspace_init(params),
            methods::AGENT_CONFIG_GET => self.agent_config_get(),
            methods::AGENT_CONFIG_SET => self.agent_config_set(params),
            methods::AGENT_LIST => self.agent_list(),
            methods::SKILL_LIST => self.skill_list(params),
            methods::SKILL_GET => self.skill_get(params),
            methods::MCP_LIST => self.mcp_list(),
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
            name: "sandbox_probe".to_owned(),
            ok: sandbox.available,
            detail: sandbox.probe_detail.clone(),
        });
        checks.push(DoctorCheck {
            name: "sandbox_capabilities".to_owned(),
            ok: true,
            detail: format!(
                "os_isolation={} read={} write={} children={} network={} | {}",
                sandbox.capabilities.os_isolation,
                sandbox.capabilities.filesystem_read_restriction,
                sandbox.capabilities.filesystem_write_restriction,
                sandbox.capabilities.child_process_containment,
                sandbox.capabilities.network_isolation,
                sandbox.capabilities.notes.join("; ")
            ),
        });
        checks.push(DoctorCheck {
            name: "sandbox_mxc".to_owned(),
            ok: sandbox.backend != "mxc" || sandbox.mxc_error.is_none(),
            detail: sandbox
                .mxc_error
                .clone()
                .unwrap_or_else(|| "MXC probe succeeded".to_owned()),
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
                self.config.agent.reasoning_effort.as_str(),
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
                    approval_rules: data.approval_rules.clone(),
                },
            );
        }
        self.current_session = Some(session_id.clone());
        let _ = self.hub.emit(
            &session_id,
            CoreEvent::SessionCreated {
                session_id: session_id.clone(),
            },
        );
        to_value(session_info(
            &data.meta,
            0,
            self.config.agent.reasoning_effort.as_str(),
        ))
    }

    fn session_fork(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionResumeParams = parse_params(params, "session.fork")?;
        let source = self.store.load(&params.session_id).map_err(store_error)?;
        let mut fork = self
            .store
            .create(
                &source.meta.workspace,
                &source.meta.provider,
                &source.meta.model,
                source.meta.reasoning_effort.as_deref().unwrap_or("medium"),
            )
            .map_err(store_error)?;
        fork.messages = source.messages;
        fork.approval_rules = source.approval_rules;
        fork.meta.name = source.meta.name.map(|name| format!("{name} (fork)"));
        self.store.save(&fork).map_err(store_error)?;
        to_value(json!({
            "session_id": fork.meta.id,
            "forked_from": params.session_id,
        }))
    }

    fn session_resume(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionResumeParams = parse_params(params, "session.resume")?;
        {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            if let Some(state) = sessions.get(&params.session_id) {
                self.current_session = Some(params.session_id.clone());
                return to_value(session_info(
                    &state.meta,
                    state.messages.len(),
                    self.config.agent.reasoning_effort.as_str(),
                ));
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
        let info = session_info(
            &data.meta,
            messages.len(),
            self.config.agent.reasoning_effort.as_str(),
        );
        let session_id = params.session_id;
        let mut sessions = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        sessions.insert(
            session_id.clone(),
            SessionState {
                meta: data.meta,
                messages,
                active_turn: None,
                approval_rules: data.approval_rules,
            },
        );
        drop(sessions);
        self.current_session = Some(session_id);
        to_value(info)
    }

    fn session_history(&self, params: Value) -> Result<Value, RpcError> {
        let params: SessionHistoryParams = parse_params(params, "session.history")?;
        let data = self.store.load(&params.session_id).map_err(store_error)?;
        let events = read_session_events(
            &self
                .store
                .root()
                .join(&params.session_id)
                .join("events.jsonl"),
        )
        .map_err(store_error)?;
        let history = build_session_history(&data.messages, &events);
        let start = history.len().saturating_sub(params.limit);
        to_value(SessionHistoryResult {
            session_id: params.session_id,
            messages: history[start..].to_vec(),
        })
    }

    fn session_list(&self) -> Result<Value, RpcError> {
        let sessions = self.store.list().map_err(store_error)?;
        let reasoning = self.config.agent.reasoning_effort.as_str();
        to_value(SessionListResult {
            sessions: sessions
                .iter()
                .map(|meta| session_info(meta, meta.message_count, reasoning))
                .collect(),
        })
    }

    fn session_delete(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionDeleteParams = parse_params(params, methods::SESSION_DELETE)?;
        if self.current_session.as_deref() == Some(params.session_id.as_str()) {
            return Err(rpc_error(
                error_codes::INTERNAL_ERROR,
                "cannot delete the active session",
            ));
        }
        self.store.delete(&params.session_id).map_err(store_error)?;
        self.sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .remove(&params.session_id);
        to_value(SessionDeleteResult { deleted: true })
    }

    fn session_rename(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionRenameParams = parse_params(params, methods::SESSION_RENAME)?;
        let busy = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .get(&params.session_id)
            .is_some_and(|state| state.active_turn.is_some());
        if busy {
            return Err(rpc_error(
                error_codes::TURN_IN_PROGRESS,
                "cannot rename a running session",
            ));
        }
        let meta = self
            .store
            .rename(&params.session_id, &params.name)
            .map_err(store_error)?;
        let info = session_info(
            &meta,
            meta.message_count,
            self.config.agent.reasoning_effort.as_str(),
        );
        if let Some(state) = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .get_mut(&params.session_id)
        {
            state.meta = meta;
        }
        to_value(info)
    }

    fn session_status(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionStatusParams = parse_params(params, methods::SESSION_STATUS)?;
        let session_id = self.resolve_session_id(params.session_id.as_deref())?;
        self.ensure_session_loaded(&session_id)?;
        let (workspace, provider, model, turn_active) = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let state = sessions.get(&session_id).ok_or_else(|| {
                rpc_error(
                    error_codes::SESSION_NOT_FOUND,
                    format!("session not found: {session_id}"),
                )
            })?;
            (
                state.meta.workspace.clone(),
                state.meta.provider.clone(),
                state.meta.model.clone(),
                state.active_turn.is_some(),
            )
        };
        let (_, metadata) = self
            .router
            .get(Some(&provider), Some(&model))
            .map_err(provider_error)?;
        to_value(SessionStatusResult {
            session_id,
            workspace,
            provider,
            model,
            context_window: metadata.context_window,
            compact_threshold: metadata.compact_threshold,
            max_output_tokens: metadata.max_output_tokens,
            turn_active,
            approval_mode: self.config.agent.approval_mode,
            sandbox: self.sandbox_summary(),
        })
    }

    fn session_compact(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SessionCompactParams = parse_params(params, methods::SESSION_COMPACT)?;
        let session_id = self.resolve_session_id(params.session_id.as_deref())?;
        self.ensure_session_loaded(&session_id)?;
        let (messages, metadata) = {
            let sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let state = sessions.get(&session_id).ok_or_else(|| {
                rpc_error(
                    error_codes::SESSION_NOT_FOUND,
                    format!("session not found: {session_id}"),
                )
            })?;
            if let Some(active) = &state.active_turn {
                return Err(rpc_error(
                    error_codes::TURN_IN_PROGRESS,
                    format!("turn already running: {active}"),
                ));
            }
            let (provider, metadata) = self
                .router
                .get(Some(&state.meta.provider), Some(&state.meta.model))
                .map_err(provider_error)?;
            let policy = Arc::new(
                PolicyEngine::new(
                    PathBuf::from(&state.meta.workspace),
                    self.effective_policy_config(),
                )
                .map_err(|error| rpc_error(error_codes::INVALID_PARAMS, error.to_string()))?,
            );
            let agent = Agent::new(
                provider,
                metadata.clone(),
                self.tools.clone(),
                policy,
                self.sandbox.clone(),
                self.audit.clone(),
                AgentConfig {
                    max_iterations: self.config.agent.max_iterations.max(1),
                    max_tool_calls_per_turn: 32,
                    max_tool_output_bytes: self.config.agent.max_tool_output_bytes,
                    command_timeout: Duration::from_secs(
                        self.config.agent.command_timeout_secs.max(1),
                    ),
                    max_total_tokens: 0,
                    tool_access: self.tool_access(),
                },
            );
            let compacted = agent.compact_history(&state.messages).map_err(|error| {
                rpc_error(
                    error_codes::INVALID_PARAMS,
                    format!("cannot compact session: {error}"),
                )
            })?;
            (compacted, metadata)
        };
        let omitted_messages = messages.omitted_messages;
        let estimated_tokens = messages.estimated_tokens;
        let history = messages.history;
        {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let state = sessions.get_mut(&session_id).ok_or_else(|| {
                rpc_error(
                    error_codes::SESSION_NOT_FOUND,
                    format!("session not found: {session_id}"),
                )
            })?;
            if let Some(active) = &state.active_turn {
                return Err(rpc_error(
                    error_codes::TURN_IN_PROGRESS,
                    format!("turn already running: {active}"),
                ));
            }
            state.messages = history;
            let data = SessionData {
                meta: state.meta.clone(),
                messages: state
                    .messages
                    .iter()
                    .map(|message| serde_json::to_value(message).unwrap_or(Value::Null))
                    .collect(),
                approval_rules: state.approval_rules.clone(),
            };
            self.store.save(&data).map_err(store_error)?;
        }
        to_value(SessionCompactResult {
            omitted_messages,
            estimated_tokens,
            context_window: metadata.context_window,
            compact_threshold: metadata.compact_threshold,
        })
    }

    fn resolve_session_id(&self, requested: Option<&str>) -> Result<String, RpcError> {
        if let Some(session_id) = requested.filter(|session_id| !session_id.is_empty()) {
            return Ok(session_id.to_owned());
        }
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if let Some(session_id) = &self.current_session {
            if sessions.contains_key(session_id) {
                return Ok(session_id.clone());
            }
        }
        if let Some((session_id, _)) = sessions
            .iter()
            .filter(|(_, state)| state.active_turn.is_some())
            .max_by_key(|(_, state)| &state.meta.updated_at)
        {
            return Ok(session_id.clone());
        }
        if let Some((session_id, _)) = sessions
            .iter()
            .max_by_key(|(_, state)| &state.meta.updated_at)
        {
            return Ok(session_id.clone());
        }
        Err(rpc_error(
            error_codes::SESSION_NOT_FOUND,
            "no active session",
        ))
    }

    fn ensure_session_loaded(&mut self, session_id: &str) -> Result<(), RpcError> {
        let loaded = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .contains_key(session_id);
        if loaded {
            self.current_session = Some(session_id.to_owned());
            return Ok(());
        }
        self.session_resume(json!({ "session_id": session_id }))
            .map(|_| ())
    }

    fn session_workspace(&mut self, session_id: &str) -> Result<PathBuf, RpcError> {
        self.ensure_session_loaded(session_id)?;
        self.sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .get(session_id)
            .map(|state| PathBuf::from(&state.meta.workspace))
            .ok_or_else(|| {
                rpc_error(
                    error_codes::SESSION_NOT_FOUND,
                    format!("session not found: {session_id}"),
                )
            })
    }

    fn turn_start(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: TurnStartParams = parse_params(params, "turn.start")?;
        let mut quality = match params.quality.as_deref() {
            Some(raw) => QualityMode::parse(raw).ok_or_else(|| {
                rpc_error(
                    error_codes::INVALID_PARAMS,
                    format!("unknown quality mode: {raw}"),
                )
            })?,
            None => self.config.agent.quality,
        };
        let default_agent = if quality == QualityMode::Deep {
            agents::DEEP_ID
        } else {
            AgentPreset::Build.as_str()
        };
        let requested_agent = params.agent.as_deref().unwrap_or(default_agent);
        let profile = agents::find(&self.agent_profiles, requested_agent)
            .cloned()
            .ok_or_else(|| {
                rpc_error(
                    error_codes::INVALID_PARAMS,
                    format!("unknown agent preset: {requested_agent}"),
                )
            })?;
        if profile.id == agents::DEEP_ID {
            quality = QualityMode::Deep;
        }
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
                .or_else(|| profile.provider.clone())
                .unwrap_or_else(|| state.meta.provider.clone());
            let requested_model = params
                .model
                .clone()
                .or_else(|| profile.model.clone())
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

        let skills_summary =
            skills::summary_prompt(&skills::discover(&self.context.home, Path::new(&workspace)));
        let policy = Arc::new(
            PolicyEngine::new(PathBuf::from(&workspace), self.effective_policy_config())
                .map_err(|error| rpc_error(error_codes::INVALID_PARAMS, error.to_string()))?,
        );
        if let Err(error) = set_turn_state(
            &self.sessions,
            &self.store,
            &params.session_id,
            TURN_STATE_WORKING,
        ) {
            let mut sessions = self
                .sessions
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            if let Some(state) = sessions.get_mut(&params.session_id) {
                state.active_turn = None;
            }
            return Err(store_error(error));
        }
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
        let router = self.router.clone();
        let agent_settings = self.config.agent.clone();
        let global_tool_access = self.tool_access();
        let tool_access = capped_tool_access(global_tool_access, profile.policy);
        let agent_config = AgentConfig {
            max_iterations: profile
                .max_iterations
                .unwrap_or(self.config.agent.max_iterations)
                .max(1),
            max_tool_calls_per_turn: 32,
            max_tool_output_bytes: self.config.agent.max_tool_output_bytes,
            command_timeout: Duration::from_secs(self.config.agent.command_timeout_secs.max(1)),
            max_total_tokens: 0,
            tool_access,
        };
        let is_deep = profile.id == agents::DEEP_ID;
        let prompt_prefix = profile.prompt_prefix.clone();
        let system_prompt = profile.system.clone().unwrap_or_default();

        let thread_session = session_id.clone();
        let thread_turn = turn_id.clone();
        let thread_metadata = metadata.clone();
        let thread_cancel = cancel.clone();
        let handle = thread::spawn(move || {
            let sink = SessionSink {
                hub: hub.clone(),
                session_id: thread_session.clone(),
            };
            let gate = CoreApprovalGate {
                broker: approvals,
                hub: hub.clone(),
                sessions: sessions.clone(),
                session_id: thread_session.clone(),
                cancel: thread_cancel.clone(),
            };
            let mut history = history;
            let user_event = sink.emit(CoreEvent::UserMessage {
                turn_id: thread_turn.clone(),
                text: prompt.clone(),
            });
            let result = if let Err(error) = user_event {
                Err(error)
            } else if is_deep {
                DeepTurn {
                    router,
                    settings: agent_settings,
                    tools,
                    policy,
                    sandbox,
                    audit,
                    sink,
                    gate,
                    cancel: thread_cancel,
                    session_id: thread_session.clone(),
                    base_turn_id: thread_turn.clone(),
                    default_provider: thread_metadata.provider.clone(),
                    default_model: thread_metadata.model.clone(),
                    task: format!("{skills_summary}{prompt_prefix}{prompt}"),
                    workspace: PathBuf::from(&workspace),
                    tool_access,
                }
                .run(&mut history)
            } else {
                let agent = Agent::new(
                    provider,
                    thread_metadata.clone(),
                    tools,
                    policy,
                    sandbox,
                    audit,
                    agent_config,
                )
                .with_system_prompt(&system_prompt);
                let prompt = format!("{skills_summary}{prompt_prefix}{prompt}");
                let prompt = if quality == QualityMode::Fast {
                    format!("Work quickly and keep the response concise.\n{prompt}")
                } else {
                    prompt
                };
                agent.run_turn(
                    &thread_session,
                    &thread_turn,
                    &prompt,
                    &mut history,
                    thread_cancel,
                    &sink,
                    &gate,
                )
            };
            if let Err(error) = &result {
                let terminal_emitted = !is_deep
                    && matches!(error, AgentError::Provider(_) | AgentError::MaxIterations);
                if !terminal_emitted {
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
                    state.meta.turn_state = TURN_STATE_IDLE.to_owned();
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
                        approval_rules: state.approval_rules.clone(),
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

    fn permissions_get(&self) -> Result<Value, RpcError> {
        to_value(self.permissions_result())
    }

    fn permissions_set(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: PermissionsSetParams = parse_params(params, methods::PERMISSIONS_SET)?;
        config::permissions_set(&self.context.config_path, params.approval_mode)
            .map_err(config_write_error)?;
        self.config.agent.approval_mode = params.approval_mode;
        let (edits, shell, plugins) = config::approval_flags(params.approval_mode);
        self.config.policy.auto_approve_edits = edits;
        self.config.policy.auto_approve_shell = shell;
        self.config.policy.auto_approve_plugins = plugins;
        let mut audit = AuditRecord::new(
            "core",
            "",
            "",
            "permissions",
            "set",
            params.approval_mode.as_str(),
            "allow",
        );
        audit.ok = true;
        self.audit
            .record(&audit)
            .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))?;
        to_value(self.permissions_result())
    }

    fn permissions_result(&self) -> PermissionsResult {
        PermissionsResult {
            approval_mode: self.config.agent.approval_mode,
            policy_summary: policy_summary(self.config.agent.approval_mode).to_owned(),
            sandbox: self.sandbox_summary(),
        }
    }

    fn sandbox_summary(&self) -> SandboxSummary {
        let status = self.sandbox.status();
        SandboxSummary {
            backend: status.backend.clone(),
            strict: status.strict,
            capabilities: SandboxCapabilitiesSummary {
                os_isolation: status.capabilities.os_isolation,
                filesystem_read_restriction: status.capabilities.filesystem_read_restriction,
                filesystem_write_restriction: status.capabilities.filesystem_write_restriction,
                child_process_containment: status.capabilities.child_process_containment,
                network_isolation: status.capabilities.network_isolation,
                notes: status.capabilities.notes.clone(),
            },
        }
    }

    fn effective_policy_config(&self) -> kl_policy::PolicyConfig {
        let mut policy = self.config.policy.clone();
        match self.config.agent.approval_mode {
            ApprovalMode::AcceptEdits => {
                policy.auto_approve_edits = true;
                policy.auto_approve_shell = false;
                policy.auto_approve_plugins = false;
            }
            ApprovalMode::DontAsk | ApprovalMode::Auto => {
                policy.auto_approve_edits = true;
                policy.auto_approve_shell = true;
                policy.auto_approve_plugins = true;
            }
            ApprovalMode::Ask | ApprovalMode::Plan | ApprovalMode::ReadOnly => {}
        }
        policy
    }

    fn tool_access(&self) -> ToolAccess {
        if matches!(
            self.config.agent.approval_mode,
            ApprovalMode::Plan | ApprovalMode::ReadOnly
        ) {
            ToolAccess::ReadOnly
        } else {
            ToolAccess::Full
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

    fn review_run(&self, params: Value) -> Result<Value, RpcError> {
        let params: ReviewRunParams = parse_params(params, methods::REVIEW_RUN)?;
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
        let mode = review_mode(&params)?;
        let context = collect_context(&workspace, params.base.clone()).map_err(review_error)?;
        let mut findings = scan_local(&context);
        let records = self
            .audit
            .tail(1000)
            .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))?;
        findings.extend(runtime_findings(&records));
        findings.extend(self.llm_review(&context, mode));
        let report = finalize(mode, context.changed_files.len(), findings);
        to_value(review_result(report))
    }

    fn llm_review(&self, context: &ReviewContext, mode: ReviewMode) -> Vec<Finding> {
        let prompt = model_prompt(context, mode);
        let result: Result<String, String> = self
            .router
            .get(None, None)
            .map_err(|error| error.to_string())
            .and_then(|(provider, metadata)| {
                let (available, detail) = provider.available();
                if !available {
                    return Err(format!(
                        "provider `{}` is unavailable: {detail}",
                        provider.id()
                    ));
                }
                let mut request =
                    ChatRequest::new(metadata.model.clone(), vec![ChatMessage::user(prompt)]);
                request.system =
                    "You are a code review model. Return only the requested JSON array.".to_owned();
                request.max_tokens = metadata.max_output_tokens;
                provider.begin_turn();
                stream_review_chat(
                    provider,
                    request,
                    CancellationToken::new(),
                    Duration::from_secs(self.config.agent.review_timeout_secs.max(1)),
                )
                .map(|outcome| outcome.text)
            });
        match result {
            Ok(text) => parse_model_findings(&text).unwrap_or_else(|error| {
                vec![llm_warning(
                    &format!("LLM review response could not be parsed: {error}"),
                    "Run the review again or inspect the raw model response.",
                )]
            }),
            Err(error) => vec![llm_warning(
                &format!("LLM review unavailable: {error}"),
                "Configure an available provider to enable model-assisted review.",
            )],
        }
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

    fn agent_config_get(&self) -> Result<Value, RpcError> {
        to_value(AgentConfigResult {
            reasoning_effort: self.config.agent.reasoning_effort,
        })
    }

    fn agent_config_set(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: AgentConfigSetParams = parse_params(params, methods::AGENT_CONFIG_SET)?;
        config::agent_config_set(&self.context.config_path, params.reasoning_effort)
            .map_err(config_write_error)?;
        self.config.agent.reasoning_effort = params.reasoning_effort;
        self.agent_config_get()
    }

    fn agent_list(&self) -> Result<Value, RpcError> {
        let agents = self
            .agent_profiles
            .iter()
            .map(|profile| AgentInfo {
                id: profile.id.clone(),
                name: profile.name.clone(),
                description: profile.description.clone(),
                policy: agents::policy_label(profile.policy).to_owned(),
                source: profile.source.clone(),
                built_in: profile.built_in,
            })
            .collect();
        to_value(AgentListResult { agents })
    }

    fn skill_list(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SkillListParams = parse_params(params, methods::SKILL_LIST)?;
        let SkillListParams {
            session_id,
            workspace,
        } = params;
        let workspace = if let Some(session_id) = session_id.as_deref() {
            self.session_workspace(session_id)?
        } else if let Some(workspace) = workspace {
            let path = PathBuf::from(workspace);
            if !path.is_dir() {
                return Err(rpc_error(
                    error_codes::INVALID_PARAMS,
                    "skill.list workspace is not a directory",
                ));
            }
            path
        } else {
            return Err(rpc_error(
                error_codes::INVALID_PARAMS,
                "skill.list requires session_id or workspace",
            ));
        };
        let skills = skills::discover(&self.context.home, &workspace)
            .into_iter()
            .map(|skill| SkillInfo {
                name: skill.name,
                description: skill.description,
            })
            .collect();
        to_value(SkillListResult { skills })
    }

    fn skill_get(&mut self, params: Value) -> Result<Value, RpcError> {
        let params: SkillGetParams = parse_params(params, methods::SKILL_GET)?;
        let workspace = self.session_workspace(&params.session_id)?;
        let content =
            skills::load_body(&self.context.home, &workspace, &params.name).map_err(skill_error)?;
        to_value(SkillGetResult {
            name: params.name,
            content,
        })
    }

    fn mcp_list(&self) -> Result<Value, RpcError> {
        let tools = self
            .tools
            .snapshot()
            .into_iter()
            .filter(|tool| tool.source() == ToolSource::Mcp)
            .map(|tool| McpToolInfo {
                name: tool.name().to_owned(),
                description: tool.description().to_owned(),
                source: "mcp".to_owned(),
            })
            .collect();
        to_value(McpListResult { tools })
    }

    fn workspace_init(&self, params: Value) -> Result<Value, RpcError> {
        const AGENTS_TEMPLATE: &str = "# AGENTS.md\n\n## Project Instructions\n\n- Keep changes focused on the requested task.\n- Follow the repository's existing conventions.\n- Run relevant tests before finishing.\n";

        let params: WorkspaceInitParams = parse_params(params, methods::WORKSPACE_INIT)?;
        let (workspace, requested) = match self.loaded_workspace_root() {
            Some(workspace) => {
                let workspace = dunce::canonicalize(&workspace).map_err(|error| {
                    rpc_error(
                        error_codes::INVALID_PARAMS,
                        format!("workspace is not accessible: {error}"),
                    )
                })?;
                let requested = params
                    .path
                    .as_deref()
                    .map(|path| workspace.join(path))
                    .unwrap_or_else(|| workspace.clone());
                (workspace, requested)
            }
            None => {
                let root = match params.path.as_deref() {
                    Some(path) => PathBuf::from(path),
                    None => std::env::current_dir().map_err(|error| {
                        rpc_error(error_codes::INTERNAL_ERROR, error.to_string())
                    })?,
                };
                let root = dunce::canonicalize(&root).map_err(|error| {
                    rpc_error(
                        error_codes::INVALID_PARAMS,
                        format!("workspace is not accessible: {error}"),
                    )
                })?;
                (root.clone(), root)
            }
        };
        let directory = dunce::canonicalize(&requested).map_err(|error| {
            rpc_error(
                error_codes::INVALID_PARAMS,
                format!("workspace path is not accessible: {error}"),
            )
        })?;
        if !directory.starts_with(&workspace) {
            return Err(rpc_error(
                error_codes::POLICY_DENIED,
                "workspace.init path is outside the workspace",
            ));
        }
        if !directory.is_dir() {
            return Err(rpc_error(
                error_codes::INVALID_PARAMS,
                "workspace.init path must be a directory",
            ));
        }
        let agents_path = directory.join("AGENTS.md");
        match std::fs::symlink_metadata(&agents_path) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(rpc_error(
                    error_codes::POLICY_DENIED,
                    "AGENTS.md must not be a symlink",
                ));
            }
            Ok(metadata) if !metadata.is_file() => {
                return Err(rpc_error(
                    error_codes::INVALID_PARAMS,
                    "AGENTS.md must be a regular file",
                ));
            }
            Ok(_) if !params.force => {
                return to_value(WorkspaceInitResult {
                    path: agents_path.display().to_string(),
                    created: false,
                });
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => {
                return Err(rpc_error(error_codes::INTERNAL_ERROR, error.to_string()));
            }
        }
        write_file_atomic(&agents_path, AGENTS_TEMPLATE.as_bytes())
            .map_err(|error| rpc_error(error_codes::INTERNAL_ERROR, error.to_string()))?;
        to_value(WorkspaceInitResult {
            path: agents_path.display().to_string(),
            created: true,
        })
    }

    fn loaded_workspace_root(&self) -> Option<PathBuf> {
        let sessions = self
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if let Some(session_id) = &self.current_session {
            if let Some(state) = sessions.get(session_id) {
                return Some(PathBuf::from(&state.meta.workspace));
            }
        }
        sessions
            .values()
            .max_by_key(|state| &state.meta.updated_at)
            .map(|state| PathBuf::from(&state.meta.workspace))
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

// Workspace the MCP servers are started in. Sessions pick their own
// workspace later; at startup the process directory is the only known root.
// shortcut: MCP servers use the startup directory as their sandbox workspace;
// move the spawn into the session turn when MCP servers become per-session.
fn mcp_workspace(context: &ServerContext) -> PathBuf {
    std::env::current_dir()
        .ok()
        .and_then(|directory| dunce::canonicalize(directory).ok())
        .unwrap_or_else(|| context.home.clone())
}

fn register_mcp_tools(
    home: &Path,
    tools: &Arc<ToolRegistry>,
    timeout: Duration,
    sandbox: &SandboxManager,
    workspace: &Path,
) {
    let config = match mcp::load_config(home) {
        Ok(config) => config,
        Err(error) => {
            eprintln!(
                "mcp: cannot read {}: {error}",
                home.join("mcp.toml").display()
            );
            return;
        }
    };
    let policy = SandboxPolicy::workspace(workspace);
    for (name, server) in config.enabled_servers() {
        let client = match mcp::McpClient::connect(server, timeout, sandbox, &policy) {
            Ok(client) => Arc::new(client),
            Err(error) => {
                eprintln!("mcp: server {name} unavailable: {error}");
                continue;
            }
        };
        match client.list_tools() {
            Ok(specs) => {
                for spec in specs {
                    tools.register(Arc::new(mcp::McpTool::new(name, spec, client.clone())));
                }
            }
            Err(error) => eprintln!("mcp: server {name} tools/list failed: {error}"),
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
            let metadata = metadata.clone().with_catalog(kl_provider::lookup_model(
                &metadata.provider,
                &metadata.model,
            ));
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
            routes.push((metadata, bound as Arc<dyn Provider>));
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
        let scripted = if config.agent.scripted_sequential {
            ScriptedProvider::from_file_sequential("scripted", &path)
        } else {
            ScriptedProvider::from_file("scripted", &path)
        }
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

fn session_info(meta: &SessionMeta, message_count: usize, fallback_reasoning: &str) -> SessionInfo {
    SessionInfo {
        session_id: meta.id.clone(),
        workspace: meta.workspace.clone(),
        provider: meta.provider.clone(),
        model: meta.model.clone(),
        reasoning_effort: meta
            .reasoning_effort
            .clone()
            .unwrap_or_else(|| fallback_reasoning.to_owned()),
        created_at: meta.created_at.clone(),
        updated_at: meta.updated_at.clone(),
        message_count,
        turn_state: meta.turn_state.clone(),
        status: meta.status.clone(),
        task: meta.task.clone(),
        prompt: meta.prompt.clone(),
        last_message: meta.last_message.clone(),
    }
}

#[derive(Debug, serde::Deserialize)]
struct StoredSessionEvent {
    seq: u64,
    at: String,
    event: CoreEvent,
}

fn read_session_events(path: &Path) -> Result<Vec<StoredSessionEvent>, StoreError> {
    if !path.exists() {
        return Ok(Vec::new());
    }
    let text = std::fs::read_to_string(path)?;
    // A crash can leave a partial final line; keep the earlier valid records.
    Ok(text
        .lines()
        .filter_map(|line| serde_json::from_str::<StoredSessionEvent>(line).ok())
        .collect())
}

fn build_session_history(
    messages: &[Value],
    events: &[StoredSessionEvent],
) -> Vec<SessionHistoryEntry> {
    let mut entries = Vec::new();
    let mut cursor = 0;
    for message in messages {
        let role = message
            .get("role")
            .and_then(Value::as_str)
            .unwrap_or_default();
        match role {
            "user" => {
                let text = history_message_text(message);
                if text.trim().is_empty() {
                    continue;
                }
                let event = next_history_event(events, &mut cursor, |event| {
                    matches!(event, CoreEvent::TurnStarted { .. })
                });
                entries.push(history_entry("user", text, event, None));
            }
            "assistant" => {
                let text = history_message_text(message);
                if text.trim().is_empty() {
                    continue;
                }
                let event = next_assistant_event(events, &mut cursor);
                entries.push(history_entry("assistant", text, event, None));
            }
            "tool" => {
                let event = next_history_event(events, &mut cursor, |event| {
                    matches!(event, CoreEvent::ToolCompleted { .. })
                });
                if let Some(event) = event {
                    if let CoreEvent::ToolCompleted {
                        name, ok, summary, ..
                    } = &event.event
                    {
                        entries.push(history_entry(
                            "tool",
                            String::new(),
                            Some(event),
                            Some(SessionHistoryToolSummary {
                                name: name.clone(),
                                ok: *ok,
                                summary: summary.clone(),
                            }),
                        ));
                    }
                }
            }
            _ => {}
        }
    }
    entries
}

fn next_assistant_event<'a>(
    events: &'a [StoredSessionEvent],
    cursor: &mut usize,
) -> Option<&'a StoredSessionEvent> {
    while *cursor < events.len() {
        match &events[*cursor].event {
            CoreEvent::TurnDelta { .. } => {
                let mut anchor = &events[*cursor];
                *cursor += 1;
                while *cursor < events.len() {
                    match &events[*cursor].event {
                        CoreEvent::TurnDelta { .. } => {
                            anchor = &events[*cursor];
                            *cursor += 1;
                        }
                        CoreEvent::TurnReasoning { .. } => {
                            *cursor += 1;
                        }
                        _ => break,
                    }
                }
                return Some(anchor);
            }
            CoreEvent::TurnCompleted { .. } => {
                let anchor = &events[*cursor];
                *cursor += 1;
                return Some(anchor);
            }
            _ => *cursor += 1,
        }
    }
    None
}

fn next_history_event<'a>(
    events: &'a [StoredSessionEvent],
    cursor: &mut usize,
    is_match: impl Fn(&CoreEvent) -> bool,
) -> Option<&'a StoredSessionEvent> {
    while *cursor < events.len() {
        let index = *cursor;
        *cursor += 1;
        if is_match(&events[index].event) {
            return Some(&events[index]);
        }
    }
    None
}

fn history_entry(
    role: &str,
    text: String,
    event: Option<&StoredSessionEvent>,
    tool: Option<SessionHistoryToolSummary>,
) -> SessionHistoryEntry {
    SessionHistoryEntry {
        role: role.to_owned(),
        text,
        timestamp: event.map(|event| event.at.clone()),
        seq: event.map(|event| event.seq),
        tool,
    }
}

fn history_message_text(value: &Value) -> String {
    if let Some(text) = value.get("content").and_then(Value::as_str) {
        return text.to_owned();
    }
    value
        .get("content")
        .and_then(Value::as_array)
        .map(|parts| {
            parts
                .iter()
                .filter(|part| part.get("type").and_then(Value::as_str) == Some("text"))
                .filter_map(|part| part.get("data").and_then(Value::as_str))
                .collect::<Vec<_>>()
                .join("")
        })
        .unwrap_or_default()
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

fn stream_review_chat(
    provider: Arc<dyn Provider>,
    request: ChatRequest,
    cancel: CancellationToken,
    timeout: Duration,
) -> Result<ChatOutcome, String> {
    let (sender, receiver) = mpsc::channel();
    let worker_token = cancel.clone();
    let mut worker = Some(thread::spawn(move || {
        let result = provider.stream_chat(&request, &worker_token, &mut |_event: ChatEvent| {});
        let _ = sender.send(result);
    }));
    let deadline = Instant::now() + timeout;
    loop {
        if cancel.is_cancelled() {
            cancel.cancel();
            join_review_worker(&mut worker, Duration::from_secs(2));
            return Err("review cancelled".to_owned());
        }
        if Instant::now() >= deadline {
            cancel.cancel();
            join_review_worker(&mut worker, Duration::from_secs(2));
            return Err("review timed out".to_owned());
        }
        match receiver.recv_timeout(Duration::from_millis(50)) {
            Ok(Ok(outcome)) => {
                join_review_worker(&mut worker, Duration::from_secs(1));
                return Ok(outcome);
            }
            Ok(Err(error)) => {
                join_review_worker(&mut worker, Duration::from_secs(1));
                return Err(error.to_string());
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                cancel.cancel();
                join_review_worker(&mut worker, Duration::from_secs(2));
                return Err("review worker disconnected".to_owned());
            }
        }
    }
}

fn join_review_worker(worker: &mut Option<JoinHandle<()>>, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if worker.as_ref().is_some_and(|handle| handle.is_finished()) {
            if let Some(handle) = worker.take() {
                let _ = handle.join();
            }
            return true;
        }
        thread::sleep(Duration::from_millis(10));
    }
    false
}

fn review_mode(params: &ReviewRunParams) -> Result<ReviewMode, RpcError> {
    if params.strict {
        return Ok(ReviewMode::Strict);
    }
    match params.mode.as_deref().unwrap_or("standard") {
        "quick" => Ok(ReviewMode::Quick),
        "standard" => Ok(ReviewMode::Standard),
        "strict" => Ok(ReviewMode::Strict),
        other => Err(rpc_error(
            error_codes::INVALID_PARAMS,
            format!("unknown review mode: {other}"),
        )),
    }
}

fn review_error(error: ReviewError) -> RpcError {
    rpc_error(error_codes::INVALID_PARAMS, error.to_string())
}

fn review_result(report: ReviewReport) -> ReviewResult {
    ReviewResult {
        mode: report.mode,
        summary: report.summary,
        changed_files: report.changed_files,
        findings: report
            .findings
            .into_iter()
            .map(|finding| ReviewFinding {
                rule: finding.rule,
                source: finding.source,
                file: finding.file,
                line: finding.line,
                severity: finding.severity.as_str().to_owned(),
                confidence: finding.confidence,
                evidence: finding.evidence,
                recommendation: finding.recommendation,
                introduced: finding.introduced,
                low_confidence: finding.low_confidence,
            })
            .collect(),
    }
}

fn skill_error(error: ToolError) -> RpcError {
    let code = match &error {
        ToolError::Policy(_) => error_codes::POLICY_DENIED,
        ToolError::InvalidArgs(_) | ToolError::NotFound(_) => error_codes::INVALID_PARAMS,
        _ => error_codes::INTERNAL_ERROR,
    };
    rpc_error(code, error.to_string())
}

fn store_error(error: StoreError) -> RpcError {
    match error {
        StoreError::NotFound(id) => rpc_error(
            error_codes::SESSION_NOT_FOUND,
            format!("session not found: {id}"),
        ),
        StoreError::InvalidName => rpc_error(
            error_codes::INVALID_PARAMS,
            "session name must be 1-160 characters",
        ),
        other => rpc_error(error_codes::INTERNAL_ERROR, other.to_string()),
    }
}

fn capped_tool_access(global: ToolAccess, preset: ToolAccess) -> ToolAccess {
    if global == ToolAccess::Full {
        preset
    } else {
        global
    }
}

fn policy_summary(mode: ApprovalMode) -> &'static str {
    match mode {
        ApprovalMode::Ask => {
            "Workspace paths are enforced; actions selected by policy can require approval."
        }
        ApprovalMode::Plan => {
            "Planning is read-only; edits, shell commands, plugin actions, and git writes are denied."
        }
        ApprovalMode::AcceptEdits => {
            "Workspace edits are auto-approved; shell commands and plugins still require approval."
        }
        ApprovalMode::DontAsk | ApprovalMode::Auto => {
            "Edits, shell commands, and plugins are auto-approved within policy; workspace paths are enforced."
        }
        ApprovalMode::ReadOnly => {
            "Reads are allowed; edits, shell commands, plugin actions, and git writes are denied."
        }
    }
}

fn write_file_atomic(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("AGENTS.md");
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temporary = path.with_file_name(format!(".{name}.{}.{stamp}.tmp", std::process::id()));
    if let Err(error) = std::fs::write(&temporary, bytes) {
        let _ = std::fs::remove_file(&temporary);
        return Err(error);
    }
    if let Err(error) = std::fs::rename(&temporary, path) {
        let _ = std::fs::remove_file(&temporary);
        return Err(error);
    }
    Ok(())
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
    use std::sync::Barrier;

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

    struct BlockingApprovalProvider {
        first: Arc<Barrier>,
        release_first: Arc<Barrier>,
        second: Arc<Barrier>,
        release_second: Arc<Barrier>,
        calls: AtomicUsize,
    }

    impl Provider for BlockingApprovalProvider {
        fn id(&self) -> &str {
            "scripted"
        }

        fn kind(&self) -> &str {
            "scripted"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("scripted")]
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            if self.calls.fetch_add(1, Ordering::SeqCst) == 0 {
                self.first.wait();
                self.release_first.wait();
                Ok(ChatOutcome {
                    text: String::new(),
                    tool_calls: vec![kl_provider::ToolCall {
                        id: "call-1".to_owned(),
                        name: "write_file".to_owned(),
                        arguments: r#"{"path":"out.txt","content":"approved"}"#.to_owned(),
                    }],
                    finish_reason: FinishReason::ToolCalls,
                    usage: Usage::default(),
                })
            } else {
                self.second.wait();
                self.release_second.wait();
                Ok(ChatOutcome {
                    text: "done".to_owned(),
                    tool_calls: Vec::new(),
                    finish_reason: FinishReason::Stop,
                    usage: Usage::default(),
                })
            }
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

    #[test]
    fn turn_state_tracks_working_approval_and_completion() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;

        let (_, metadata) = core.router.get(Some("scripted"), Some("scripted")).unwrap();
        let first = Arc::new(Barrier::new(2));
        let release_first = Arc::new(Barrier::new(2));
        let second = Arc::new(Barrier::new(2));
        let release_second = Arc::new(Barrier::new(2));
        core.router.register(
            metadata,
            Arc::new(BlockingApprovalProvider {
                first: first.clone(),
                release_first: release_first.clone(),
                second: second.clone(),
                release_second: release_second.clone(),
                calls: AtomicUsize::new(0),
            }),
        );

        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        assert_eq!(created["turn_state"], TURN_STATE_IDLE);
        let listed = core.session_list().unwrap();
        assert_eq!(listed["sessions"][0]["turn_state"], TURN_STATE_IDLE);

        core.turn_start(json!({"session_id": session_id, "prompt": "write a file"}))
            .unwrap();
        first.wait();
        assert_eq!(
            session_turn_state(&core, &session_id).as_deref(),
            Some(TURN_STATE_WORKING)
        );
        release_first.wait();

        wait_for_session_turn_state(&core, &session_id, TURN_STATE_WAITING_APPROVAL);
        let approval_id = wait_for_approval_id(&buffer);
        let response = core
            .approval_respond(json!({
                "session_id": session_id,
                "approval_id": approval_id,
                "decision": "allow"
            }))
            .unwrap();
        assert_eq!(response["accepted"], true);

        wait_for_session_turn_state(&core, &session_id, TURN_STATE_WORKING);
        second.wait();
        assert_eq!(
            session_turn_state(&core, &session_id).as_deref(),
            Some(TURN_STATE_WORKING)
        );
        release_second.wait();

        wait_for_threads(&mut core);
        assert_eq!(
            session_turn_state(&core, &session_id).as_deref(),
            Some(TURN_STATE_IDLE)
        );
        let listed = core.session_list().unwrap();
        assert_eq!(listed["sessions"][0]["turn_state"], TURN_STATE_IDLE);
    }

    #[test]
    fn turn_state_survives_core_restart() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let session_id = {
            let (mut core, _) = new_test_core(config.clone(), home.path());
            core.handshaken = true;
            let created = core
                .session_create(json!({"workspace": workspace.to_string_lossy()}))
                .unwrap();
            let session_id = created["session_id"].as_str().unwrap().to_owned();
            core.store
                .set_turn_state(&session_id, TURN_STATE_WAITING_APPROVAL)
                .unwrap();
            session_id
        };

        let (mut restarted, _) = new_test_core(config, home.path());
        restarted.handshaken = true;
        let listed = restarted.session_list().unwrap();
        assert_eq!(
            listed["sessions"][0]["turn_state"],
            TURN_STATE_WAITING_APPROVAL
        );
        let resumed = restarted
            .session_resume(json!({"session_id": session_id}))
            .unwrap();
        assert_eq!(resumed["turn_state"], TURN_STATE_WAITING_APPROVAL);
    }

    use kl_provider::{ChatOutcome, FinishReason, ModelInfo, ProviderErrorKind};

    fn copy_dir(source: &Path, target: &Path) {
        std::fs::create_dir_all(target).unwrap();
        for entry in std::fs::read_dir(source).unwrap() {
            let entry = entry.unwrap();
            if entry.file_name() == ".git" {
                continue;
            }
            let destination = target.join(entry.file_name());
            if entry.file_type().unwrap().is_dir() {
                copy_dir(&entry.path(), &destination);
            } else {
                std::fs::copy(entry.path(), destination).unwrap();
            }
        }
    }

    fn test_git(workspace: &Path, args: &[&str]) {
        let status = std::process::Command::new("git")
            .arg("-C")
            .arg(workspace)
            .args([
                "-c",
                "user.email=review@example.test",
                "-c",
                "user.name=review",
            ])
            .args(args)
            .status()
            .unwrap();
        assert!(status.success(), "git {:?} failed", args);
    }

    fn output_lines(buffer: &Buffer) -> Vec<Value> {
        let bytes = buffer.0.lock().unwrap().clone();
        String::from_utf8(bytes)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect()
    }

    fn wait_for_threads(core: &mut Core) {
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline && core.threads.iter().any(|handle| !handle.is_finished()) {
            std::thread::sleep(Duration::from_millis(25));
        }
        assert!(
            core.threads.iter().all(|handle| handle.is_finished()),
            "core turn thread did not finish"
        );
        for handle in core.threads.drain(..) {
            handle.join().unwrap();
        }
    }

    fn session_turn_state(core: &Core, session_id: &str) -> Option<String> {
        core.store
            .load(session_id)
            .ok()
            .map(|data| data.meta.turn_state)
    }

    fn wait_for_session_turn_state(core: &Core, session_id: &str, expected: &str) {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if session_turn_state(core, session_id).as_deref() == Some(expected) {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "turn state did not become {expected}: {:?}",
                session_turn_state(core, session_id)
            );
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    fn wait_for_approval_id(buffer: &Buffer) -> String {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            for value in output_lines(buffer) {
                if value["params"]["event"]["type"] == "tool.approval.requested" {
                    if let Some(id) = value["params"]["event"]["data"]["approval_id"].as_str() {
                        return id.to_owned();
                    }
                }
            }
            assert!(
                Instant::now() < deadline,
                "tool approval request was not emitted"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    fn deep_config(temp: &Path) -> PathBuf {
        std::fs::write(
            temp.join("script.json"),
            r#"[
                {"text":"explored src/lib.rs:1"},
                {"text":"PLAN write out.txt"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"out.txt","content":"done"}}]},
                {"text":"IMPLEMENTATION wrote out.txt:1"},
                {"text":"REVIEW out.txt:1 evidence=done"},
                {"text":"VERIFY out.txt:1 tests passed"}
            ]"#,
        )
        .unwrap();
        let config = temp.join("kl.toml");
        std::fs::write(
            &config,
            r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
scripted_sequential = true
quality = "deep"

[policy]
auto_approve_edits = true
"#,
        )
        .unwrap();
        config
    }

    fn new_test_core(config: PathBuf, home: &Path) -> (Core, Buffer) {
        let buffer = Buffer::default();
        let core = Core::new(
            ServerContext::resolve(Some(config), Some(home.to_path_buf())),
            Emitter::new(Box::new(buffer.clone())),
        )
        .unwrap();
        (core, buffer)
    }

    fn command_config(temp: &Path) -> PathBuf {
        std::fs::write(temp.join("script.json"), r#"[{"text":"unused"}]"#).unwrap();
        let config = temp.join("kl.toml");
        std::fs::write(
            &config,
            r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
max_iterations = 2
"#,
        )
        .unwrap();
        config
    }

    #[test]
    fn session_status_reports_the_loaded_session_and_turn_state() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();

        let status = core.session_status(json!({})).unwrap();
        assert_eq!(status["session_id"], session_id);
        let expected_workspace = std::fs::canonicalize(&workspace).unwrap();
        let expected_text = expected_workspace.to_string_lossy();
        let expected_text = expected_text
            .strip_prefix(r"\\?\")
            .unwrap_or(expected_text.as_ref());
        assert_eq!(status["workspace"], expected_text);
        assert_eq!(status["provider"], "scripted");
        assert_eq!(status["model"], "scripted");
        assert_eq!(status["context_window"], 8192);
        assert_eq!(status["turn_active"], false);
        assert_eq!(status["approval_mode"], "ask");
        assert!(status["sandbox"]["backend"].as_str().is_some());
        assert!(status["sandbox"]["capabilities"].is_object());

        {
            let mut sessions = core.sessions.lock().unwrap();
            sessions.get_mut(&session_id).unwrap().active_turn = Some("turn-test".to_owned());
        }
        let active = core
            .session_status(json!({"session_id": session_id}))
            .unwrap();
        assert_eq!(active["turn_active"], true);
    }

    #[test]
    fn permissions_modes_are_hot_updated_and_persisted() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config_path.clone(), home.path());
        core.handshaken = true;

        for mode in ["ask", "plan", "acceptEdits", "dontAsk", "auto", "read-only"] {
            let result = core
                .permissions_set(json!({"approval_mode": mode}))
                .unwrap();
            assert_eq!(result["approval_mode"], mode);
            assert_eq!(core.permissions_get().unwrap(), result);
            let saved = Config::load(&config_path).unwrap();
            assert_eq!(saved.agent.approval_mode.as_str(), mode);
            let (edits, shell, plugins) = match mode {
                "acceptEdits" => (true, false, false),
                "dontAsk" | "auto" => (true, true, true),
                _ => (false, false, false),
            };
            assert_eq!(saved.policy.auto_approve_edits, edits);
            assert_eq!(saved.policy.auto_approve_shell, shell);
            assert_eq!(saved.policy.auto_approve_plugins, plugins);
            assert_eq!(
                core.tool_access(),
                if matches!(mode, "plan" | "read-only") {
                    ToolAccess::ReadOnly
                } else {
                    ToolAccess::Full
                }
            );
        }

        let error = core
            .permissions_set(json!({"approval_mode": "unknown"}))
            .unwrap_err();
        assert_eq!(error.code, error_codes::INVALID_PARAMS);
        assert_eq!(
            core.permissions_get().unwrap()["approval_mode"],
            "read-only"
        );
    }

    #[test]
    fn permissions_and_reasoning_effort_survive_restart() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        {
            let (mut core, _) = new_test_core(config_path.clone(), home.path());
            core.handshaken = true;
            assert_eq!(
                core.agent_config_get().unwrap()["reasoning_effort"],
                "default"
            );
            core.permissions_set(json!({"approval_mode": "auto"}))
                .unwrap();
            core.agent_config_set(json!({"reasoning_effort": "high"}))
                .unwrap();
        }

        let (mut restarted, _) = new_test_core(config_path, home.path());
        restarted.handshaken = true;
        assert_eq!(
            restarted.permissions_get().unwrap()["approval_mode"],
            "auto"
        );
        assert_eq!(
            restarted.agent_config_get().unwrap()["reasoning_effort"],
            "high"
        );
        let error = restarted
            .agent_config_set(json!({"reasoning_effort": "extreme"}))
            .unwrap_err();
        assert_eq!(error.code, error_codes::INVALID_PARAMS);
    }

    #[test]
    fn session_compact_reuses_agent_compaction_and_keeps_tool_pairs() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        let empty = core
            .session_compact(json!({"session_id": session_id}))
            .unwrap();
        assert_eq!(empty["omitted_messages"], 0);
        assert!(empty["estimated_tokens"].as_u64().unwrap() > 0);
        let tool_call = kl_provider::ToolCall {
            id: "latest-call".to_owned(),
            name: "read_file".to_owned(),
            arguments: r#"{"path":"README.md"}"#.to_owned(),
        };
        let mut messages = Vec::new();
        for round in 0..3 {
            messages.push(ChatMessage::user(format!("round {round}")));
            if round == 2 {
                messages.push(ChatMessage::assistant("", vec![tool_call.clone()]));
                messages.push(ChatMessage::tool("latest-call", "file contents"));
                messages.push(ChatMessage::assistant("latest answer", Vec::new()));
            } else {
                messages.push(ChatMessage::assistant(
                    format!("answer {round}"),
                    Vec::new(),
                ));
            }
        }
        {
            let mut sessions = core.sessions.lock().unwrap();
            sessions.get_mut(&session_id).unwrap().messages = messages;
        }

        let compacted = core
            .session_compact(json!({"session_id": session_id}))
            .unwrap();
        assert!(compacted["omitted_messages"].as_u64().unwrap() <= 3);
        assert!(compacted["estimated_tokens"].as_u64().unwrap() > 0);
        assert_eq!(compacted["context_window"], 8192);
        assert!((compacted["compact_threshold"].as_f64().unwrap() - 0.8).abs() < 0.001);

        let history = {
            let sessions = core.sessions.lock().unwrap();
            sessions.get(&session_id).unwrap().messages.clone()
        };
        let tool_ids: Vec<String> = history
            .iter()
            .filter_map(|message| message.tool_call_id.clone())
            .collect();
        for call in history.iter().flat_map(|message| &message.tool_calls) {
            assert!(
                tool_ids.contains(&call.id),
                "tool call {} lost its result",
                call.id
            );
        }
        assert!(
            history
                .iter()
                .any(|message| message.text() == "latest answer")
        );
        let persisted = core.store.load(&session_id).unwrap();
        assert_eq!(persisted.messages.len(), history.len());

        {
            let mut sessions = core.sessions.lock().unwrap();
            sessions.get_mut(&session_id).unwrap().active_turn = Some("turn-test".to_owned());
        }
        let active_error = core
            .session_compact(json!({"session_id": session_id}))
            .unwrap_err();
        assert_eq!(active_error.code, error_codes::TURN_IN_PROGRESS);

        let empty_home = tempfile::tempdir().unwrap();
        let (mut empty, _) = new_test_core(temp.path().join("empty.toml"), empty_home.path());
        empty.handshaken = true;
        let error = empty.session_compact(json!({})).unwrap_err();
        assert_eq!(error.code, error_codes::SESSION_NOT_FOUND);
    }

    #[test]
    fn workspace_init_creates_without_overwriting_and_rejects_escape() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        let nested = workspace.join("nested");
        let outside = temp.path().join("outside");
        std::fs::create_dir_all(&nested).unwrap();
        std::fs::create_dir_all(&outside).unwrap();
        let config = command_config(temp.path());
        let config_path = config.clone();
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        core.session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();

        let created = core
            .workspace_init(json!({"path": "nested", "force": false}))
            .unwrap();
        assert_eq!(created["created"], true);
        let agents_path = PathBuf::from(created["path"].as_str().unwrap());
        assert!(agents_path.is_file());
        std::fs::write(&agents_path, "keep me").unwrap();

        let existing = core
            .workspace_init(json!({"path": "nested", "force": false}))
            .unwrap();
        assert_eq!(existing["created"], false);
        assert_eq!(std::fs::read_to_string(&agents_path).unwrap(), "keep me");

        let forced = core
            .workspace_init(json!({"path": "nested", "force": true}))
            .unwrap();
        assert_eq!(forced["created"], true);
        assert!(
            std::fs::read_to_string(&agents_path)
                .unwrap()
                .starts_with("# AGENTS.md")
        );

        let error = core
            .workspace_init(json!({"path": outside.to_string_lossy()}))
            .unwrap_err();
        assert_eq!(error.code, error_codes::POLICY_DENIED);
        assert!(!outside.join("AGENTS.md").exists());

        let standalone_home = tempfile::tempdir().unwrap();
        let (mut standalone, _) = new_test_core(config_path, standalone_home.path());
        standalone.handshaken = true;
        let initialized = standalone
            .workspace_init(json!({"path": workspace.to_string_lossy()}))
            .unwrap();
        assert_eq!(initialized["created"], true);
        assert!(workspace.join("AGENTS.md").is_file());
    }

    #[cfg(unix)]
    #[test]
    fn workspace_init_rejects_agents_symlink() {
        use std::os::unix::fs::symlink;

        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let outside = temp.path().join("outside.md");
        std::fs::write(&outside, "outside").unwrap();
        symlink(&outside, workspace.join("AGENTS.md")).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        core.session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();

        let error = core.workspace_init(json!({"force": true})).unwrap_err();
        assert_eq!(error.code, error_codes::POLICY_DENIED);
        assert_eq!(std::fs::read_to_string(outside).unwrap(), "outside");
    }

    #[test]
    fn review_run_finds_fixture_rules_without_a_provider() {
        let temp = tempfile::tempdir().unwrap();
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        let workspace = temp.path().join("repo");
        copy_dir(&root.join("tests/fixtures/review-repo/base"), &workspace);
        test_git(&workspace, &["init"]);
        test_git(&workspace, &["add", "."]);
        test_git(&workspace, &["commit", "-m", "base"]);
        copy_dir(&root.join("tests/fixtures/review-repo/changed"), &workspace);
        let home = tempfile::tempdir().unwrap();
        let escaped = workspace.to_string_lossy().replace('\\', "\\\\");
        let input = format!(
            "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"review.run\",\"params\":{{\"workspace\":\"{escaped}\",\"mode\":\"strict\"}}}}\n"
        );
        let output = serve(&input, None, home.path().to_path_buf());
        let review = output.iter().find(|value| value["id"] == 2).unwrap();
        let rules: Vec<&str> = review["result"]["findings"]
            .as_array()
            .unwrap()
            .iter()
            .map(|finding| finding["rule"].as_str().unwrap())
            .collect();
        for expected in [
            "debug-unwrap",
            "secret-openai",
            "missing-test",
            "cross-file-command-injection",
            "llm-review-unavailable",
        ] {
            assert!(
                rules.contains(&expected),
                "missing {expected}; got {rules:?}"
            );
        }
    }

    #[test]
    fn turn_start_records_one_user_message_before_the_agent_starts() {
        let temp = tempfile::tempdir().unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": temp.path().to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();

        core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
            .unwrap();
        wait_for_threads(&mut core);

        let events = core.store.read_events(&session_id).unwrap();
        let user_messages: Vec<_> = events
            .iter()
            .filter_map(|stored| match &stored.event {
                CoreEvent::UserMessage { turn_id, text } => Some((stored.seq, turn_id, text)),
                _ => None,
            })
            .collect();
        assert_eq!(user_messages.len(), 1);
        assert_eq!(user_messages[0].2, "hello");
        let started_seq = events
            .iter()
            .find(|stored| {
                matches!(
                    &stored.event,
                    CoreEvent::TurnStarted { turn_id } if turn_id == user_messages[0].1
                )
            })
            .map(|stored| stored.seq)
            .expect("matching turn.started event");
        assert!(user_messages[0].0 < started_seq);
    }

    #[test]
    fn agent_preset_defaults_and_tool_access_are_bounded() {
        assert_eq!(
            capped_tool_access(ToolAccess::Full, AgentPreset::Plan.tool_access()),
            ToolAccess::ReadOnly
        );
        assert_eq!(
            capped_tool_access(ToolAccess::Full, AgentPreset::Verify.tool_access()),
            ToolAccess::VerifyOnly
        );
        assert_eq!(
            capped_tool_access(ToolAccess::ReadOnly, AgentPreset::Build.tool_access()),
            ToolAccess::ReadOnly
        );
        assert_eq!(
            capped_tool_access(ToolAccess::ReadOnly, AgentPreset::Deep.tool_access()),
            ToolAccess::ReadOnly
        );
    }

    #[test]
    fn agent_list_includes_custom_profiles() {
        let temp = tempfile::tempdir().unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(home.path().join("agents")).unwrap();
        std::fs::write(
            home.path().join("agents/reviewer.toml"),
            "id = \"reviewer\"\nname = \"Reviewer\"\ndescription = \"strict\"\npolicy = \"read-only\"\n",
        )
        .unwrap();
        let (core, _) = new_test_core(config, home.path());
        let list = core.agent_list().unwrap();
        let agents = list["agents"].as_array().unwrap();
        assert!(agents.iter().any(|agent| agent["id"] == "reviewer"
            && agent["policy"] == "read-only"
            && agent["built_in"] == false));
        assert!(
            agents
                .iter()
                .any(|agent| agent["id"] == "deep" && agent["built_in"] == true)
        );
    }

    #[test]
    fn turn_start_rejects_unknown_agent_preset() {
        let temp = tempfile::tempdir().unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        let error = core
            .turn_start(json!({
                "session_id": "missing",
                "prompt": "hello",
                "agent": "turbo"
            }))
            .unwrap_err();
        assert_eq!(error.code, error_codes::INVALID_PARAMS);
        assert!(error.message.contains("unknown agent preset"));
    }

    #[test]
    fn turn_start_prepends_the_agent_preset_prompt() {
        let temp = tempfile::tempdir().unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": temp.path().to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "hello",
            "agent": "plan"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let history = core
            .session_history(json!({"session_id": session_id}))
            .unwrap();
        let first = history["messages"][0]["text"].as_str().unwrap();
        assert!(first.starts_with(AgentPreset::Plan.prompt_prefix()));
        assert!(first.ends_with("hello"));
    }

    #[test]
    fn turn_start_prepends_discovered_skill_summaries() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let skill = home.path().join("skills").join("alpha");
        std::fs::create_dir_all(&skill).unwrap();
        std::fs::write(
            skill.join("SKILL.md"),
            "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
        )
        .unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "hello"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let history = core
            .session_history(json!({"session_id": session_id}))
            .unwrap();
        let first = history["messages"][0]["text"].as_str().unwrap();
        assert!(first.starts_with("alpha: Alpha description\n"));
        assert!(first.ends_with("hello"));
        assert_eq!(first.matches("Alpha description").count(), 1);
        assert!(!first.contains("SECRET BODY"));
    }

    #[test]
    fn skill_rpc_lists_summaries_and_loads_body() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let skill = workspace.join(".kl/skills/alpha");
        std::fs::create_dir_all(&skill).unwrap();
        std::fs::write(
            skill.join("SKILL.md"),
            "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
        )
        .unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();

        let list = core.skill_list(json!({"session_id": session_id})).unwrap();
        assert_eq!(list["skills"][0]["name"], "alpha");
        assert_eq!(list["skills"][0]["description"], "Alpha description");
        assert!(!list.to_string().contains("SECRET BODY"));
        assert!(!list.to_string().contains("SKILL.md"));

        let loaded = core
            .skill_get(json!({"session_id": session_id, "name": "alpha"}))
            .unwrap();
        assert_eq!(loaded["name"], "alpha");
        assert_eq!(loaded["content"], "SECRET BODY\n");

        let missing = core
            .skill_get(json!({"session_id": session_id, "name": "missing"}))
            .unwrap_err();
        assert_eq!(missing.code, error_codes::INVALID_PARAMS);
        assert!(missing.message.contains("unknown skill: missing"));
    }

    #[test]
    fn skill_rpc_lists_by_workspace_and_rejects_missing_target() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        let skill = workspace.join(".kl/skills/alpha");
        std::fs::create_dir_all(&skill).unwrap();
        std::fs::write(
            skill.join("SKILL.md"),
            "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
        )
        .unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;

        let list = core
            .skill_list(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        assert_eq!(list["skills"][0]["name"], "alpha");
        assert_eq!(list["skills"][0]["description"], "Alpha description");

        let missing = core.skill_list(json!({})).unwrap_err();
        assert_eq!(missing.code, error_codes::INVALID_PARAMS);
        assert!(missing.message.contains("session_id or workspace"));
    }

    #[test]
    fn skill_rpc_loads_home_skill_body() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let skill = home.path().join("skills/global");
        std::fs::create_dir_all(&skill).unwrap();
        std::fs::write(
            skill.join("SKILL.md"),
            "---\nname: global\ndescription: Global skill\n---\nGLOBAL BODY\n",
        )
        .unwrap();
        let (mut core, _) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();

        let list = core.skill_list(json!({"session_id": session_id})).unwrap();
        assert_eq!(list["skills"][0]["name"], "global");

        let loaded = core
            .skill_get(json!({"session_id": session_id, "name": "global"}))
            .unwrap();
        assert_eq!(loaded["name"], "global");
        assert_eq!(loaded["content"], "GLOBAL BODY\n");
    }

    #[test]
    fn mcp_list_is_empty_without_mcp_tools() {
        let temp = tempfile::tempdir().unwrap();
        let config = command_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (core, _) = new_test_core(config, home.path());
        let result = core.mcp_list().unwrap();
        assert_eq!(result["tools"], json!([]));
    }

    #[test]
    fn deep_mode_runs_five_stages_and_writes_the_workspace() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        let workspace = temp.path().to_string_lossy().to_string();
        let created = core
            .session_create(json!({"workspace": workspace}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "write done to out.txt",
            "quality": "deep"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let output = output_lines(&buffer);
        let completed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.completed")
            .expect("turn.completed event");
        assert_eq!(
            completed["params"]["event"]["data"]["status"], "completed",
            "{completed}"
        );
        let text = completed["params"]["event"]["data"]["text"]
            .as_str()
            .unwrap();
        assert!(text.contains("explorer -> planner -> implementer -> reviewer -> verifier"));
        assert!(text.contains("REVIEW out.txt:1"));
        assert!(text.contains("VERIFY out.txt:1"));
        assert_eq!(
            std::fs::read_to_string(temp.path().join("out.txt")).unwrap(),
            "done"
        );
    }

    #[test]
    fn deep_prompts_include_index_context_only_for_explorer_and_planner() {
        let context = "3 files, 4 symbols.";
        let explorer = DeepStage::Explorer.prompt("task", Some(context));
        assert!(explorer.contains(context));
        assert!(explorer.contains("Repository index"));
        let planner = DeepStage::Planner.prompt("task", Some(context));
        assert!(planner.contains(context));
        let reviewer = DeepStage::Reviewer.prompt("task", Some(context));
        assert!(!reviewer.contains(context));
        let verifier = DeepStage::Verifier.prompt("task", Some(context));
        assert!(!verifier.contains(context));
    }

    struct SlowProvider;

    impl Provider for SlowProvider {
        fn id(&self) -> &str {
            "slow"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("slow")]
        }

        fn available(&self) -> (bool, String) {
            (true, "slow provider".to_owned())
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            for _ in 0..50 {
                if cancel.is_cancelled() {
                    return Err(ProviderError::cancelled());
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            Ok(ChatOutcome {
                text: "slow".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    #[test]
    fn deep_mode_cancels_without_waiting_for_all_stages() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        core.router = ProviderRouter::new(
            vec![(
                ModelMetadata::new(
                    "slow",
                    "slow",
                    Protocol::Scripted,
                    "test://slow",
                    8192,
                    0.8,
                    4096,
                ),
                Arc::new(SlowProvider) as Arc<dyn Provider>,
            )],
            "slow",
            "slow",
        );
        core.config.agent.provider = "slow".to_owned();
        core.config.agent.model = "slow".to_owned();
        let workspace = temp.path().to_string_lossy().to_string();
        let created = core
            .session_create(json!({"workspace": workspace}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        let started = core
            .turn_start(json!({
                "session_id": session_id,
                "prompt": "cancel this deep turn",
                "quality": "deep"
            }))
            .unwrap();
        let turn_id = started["turn_id"].as_str().unwrap().to_owned();
        core.turn_cancel(json!({
            "session_id": session_id,
            "turn_id": turn_id
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let output = output_lines(&buffer);
        let completed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.completed")
            .expect("turn.completed event");
        assert_eq!(
            completed["params"]["event"]["data"]["status"], "cancelled",
            "{completed}"
        );
    }
    struct FailAfterProvider {
        calls: AtomicUsize,
    }

    impl Provider for FailAfterProvider {
        fn id(&self) -> &str {
            "fail-after"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("fail-after")]
        }

        fn available(&self) -> (bool, String) {
            (true, "fails after two calls".to_owned())
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            let call = self.calls.fetch_add(1, Ordering::Relaxed);
            if call < 2 {
                return Ok(ChatOutcome {
                    text: format!("stage {call}"),
                    tool_calls: Vec::new(),
                    finish_reason: FinishReason::Stop,
                    usage: Usage::default(),
                });
            }
            Err(ProviderError::new(ProviderErrorKind::Server, "boom"))
        }
    }

    #[test]
    fn deep_mode_reports_implementer_provider_failure() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        core.router = ProviderRouter::new(
            vec![(
                ModelMetadata::new(
                    "fail-after",
                    "fail-after",
                    Protocol::Scripted,
                    "test://fail-after",
                    8192,
                    0.8,
                    4096,
                ),
                Arc::new(FailAfterProvider {
                    calls: AtomicUsize::new(0),
                }) as Arc<dyn Provider>,
            )],
            "fail-after",
            "fail-after",
        );
        core.config.agent.provider = "fail-after".to_owned();
        core.config.agent.model = "fail-after".to_owned();
        let workspace = temp.path().to_string_lossy().to_string();
        let created = core
            .session_create(json!({"workspace": workspace}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "fail during implementation",
            "quality": "deep"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let output = output_lines(&buffer);
        let failed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.failed")
            .expect("turn.failed event");
        let message = failed["params"]["event"]["data"]["message"]
            .as_str()
            .unwrap();
        assert!(message.contains("boom"), "{message}");
    }
    struct ReviewMutationProvider {
        workspace: PathBuf,
        calls: AtomicUsize,
    }

    impl Provider for ReviewMutationProvider {
        fn id(&self) -> &str {
            "mutating-review"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("mutating-review")]
        }

        fn available(&self) -> (bool, String) {
            (true, "writes during review".to_owned())
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            let call = self.calls.fetch_add(1, Ordering::Relaxed);
            if call == 3 {
                std::fs::write(self.workspace.join("sneaky.txt"), "extra").unwrap();
            }
            Ok(ChatOutcome {
                text: format!("stage {call}"),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    fn deep_turn_output(config: PathBuf, workspace: &Path) -> Vec<Value> {
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "deep stage test",
            "quality": "deep"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        output_lines(&buffer)
    }

    #[test]
    fn deep_mode_reviewer_and_verifier_tools_are_hard_read_only() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        std::fs::write(
            temp.path().join("script.json"),
            r#"[
                {"text":"explored"},
                {"text":"plan"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"out.txt","content":"done"}}]},
                {"text":"implemented"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"sneaky.txt","content":"extra"}}]},
                {"text":"reviewed"},
                {"tool_calls":[{"name":"shell","arguments":{"command":"echo hacked > sneaky.txt","cwd":"."}}]},
                {"text":"verified"}
            ]"#,
        )
        .unwrap();
        let output = deep_turn_output(config, temp.path());
        let completed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.completed")
            .expect("turn.completed event");
        assert_eq!(completed["params"]["event"]["data"]["status"], "completed");
        assert!(!temp.path().join("sneaky.txt").exists());
        assert!(temp.path().join("out.txt").exists());
        let tool_events: Vec<&Value> = output
            .iter()
            .filter(|value| value["params"]["event"]["type"] == "tool.completed")
            .collect();
        assert!(tool_events.iter().any(|value| {
            value["params"]["event"]["data"]["name"] == "write_file"
                && value["params"]["event"]["data"]["ok"] == false
                && value["params"]["event"]["data"]["summary"]
                    .as_str()
                    .is_some_and(|text| text.contains("review-only"))
        }));
        assert!(tool_events.iter().any(|value| {
            value["params"]["event"]["data"]["name"] == "shell"
                && value["params"]["event"]["data"]["ok"] == false
                && value["params"]["event"]["data"]["summary"]
                    .as_str()
                    .is_some_and(|text| text.contains("verification stage allows only"))
        }));
    }

    #[test]
    fn deep_mode_fingerprint_catches_out_of_band_reviewer_mutation() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        let home = tempfile::tempdir().unwrap();
        let (mut core, buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        core.router = ProviderRouter::new(
            vec![(
                ModelMetadata::new(
                    "mutating-review",
                    "mutating-review",
                    Protocol::Scripted,
                    "test://mutating-review",
                    8192,
                    0.8,
                    4096,
                ),
                Arc::new(ReviewMutationProvider {
                    workspace: temp.path().to_path_buf(),
                    calls: AtomicUsize::new(0),
                }) as Arc<dyn Provider>,
            )],
            "mutating-review",
            "mutating-review",
        );
        core.config.agent.provider = "mutating-review".to_owned();
        core.config.agent.model = "mutating-review".to_owned();
        let workspace = temp.path().to_string_lossy().to_string();
        let created = core
            .session_create(json!({"workspace": workspace}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.turn_start(json!({
            "session_id": session_id,
            "prompt": "detect mutation",
            "quality": "deep"
        }))
        .unwrap();
        wait_for_threads(&mut core);
        let output = output_lines(&buffer);
        let failed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.failed")
            .expect("turn.failed event");
        let message = failed["params"]["event"]["data"]["message"]
            .as_str()
            .unwrap();
        assert!(message.contains("modified the workspace"), "{message}");
        assert!(temp.path().join("sneaky.txt").exists());
    }

    #[test]
    fn deep_mode_budgets_provider_without_usage() {
        let temp = tempfile::tempdir().unwrap();
        let config = deep_config(temp.path());
        let text = std::fs::read_to_string(&config).unwrap();
        std::fs::write(
            &config,
            text.replace(
                "quality = \"deep\"",
                "quality = \"deep\"\ndeep_token_budget = 1",
            ),
        )
        .unwrap();
        let output = deep_turn_output(config, temp.path());
        let failed = output
            .iter()
            .find(|value| value["params"]["event"]["type"] == "turn.failed")
            .expect("turn.failed event");
        let message = failed["params"]["event"]["data"]["message"]
            .as_str()
            .unwrap();
        assert!(message.contains("token budget"), "{message}");
    }

    struct BlockingReviewProvider {
        started: Arc<AtomicBool>,
    }

    impl Provider for BlockingReviewProvider {
        fn id(&self) -> &str {
            "blocking-review"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("blocking-review")]
        }

        fn available(&self) -> (bool, String) {
            (true, "blocks until cancelled".to_owned())
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            self.started.store(true, Ordering::Relaxed);
            while !cancel.is_cancelled() {
                std::thread::sleep(Duration::from_millis(10));
            }
            Err(ProviderError::cancelled())
        }
    }

    #[test]
    fn review_chat_cancellation_exits_promptly() {
        let started = Arc::new(AtomicBool::new(false));
        let cancel = CancellationToken::new();
        let request = ChatRequest::new("blocking-review", vec![ChatMessage::user("review")]);
        let thread_cancel = cancel.clone();
        let thread_started = started.clone();
        let handle = std::thread::spawn(move || {
            stream_review_chat(
                Arc::new(BlockingReviewProvider {
                    started: thread_started,
                }),
                request,
                thread_cancel,
                Duration::from_secs(5),
            )
        });
        let deadline = Instant::now() + Duration::from_secs(2);
        while !started.load(Ordering::Relaxed) && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(10));
        }
        assert!(started.load(Ordering::Relaxed));
        let cancelled_at = Instant::now();
        cancel.cancel();
        let result = handle.join().unwrap();
        assert!(result.unwrap_err().contains("cancelled"));
        assert!(cancelled_at.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn review_chat_timeout_exits_promptly() {
        let started = Arc::new(AtomicBool::new(false));
        let started_for_worker = started.clone();
        let started_at = Instant::now();
        let result = stream_review_chat(
            Arc::new(BlockingReviewProvider {
                started: started_for_worker,
            }),
            ChatRequest::new("blocking-review", vec![ChatMessage::user("review")]),
            CancellationToken::new(),
            Duration::from_millis(100),
        );
        assert!(result.unwrap_err().contains("timed out"));
        assert!(started_at.elapsed() < Duration::from_secs(1));
        assert!(started.load(Ordering::Relaxed));
    }
    #[test]
    fn review_run_timeout_returns_warning_instead_of_blocking() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "old\n").unwrap();
        test_git(temp.path(), &["init"]);
        test_git(temp.path(), &["add", "."]);
        test_git(temp.path(), &["commit", "-m", "base"]);
        std::fs::write(temp.path().join("a.txt"), "new\n").unwrap();
        let config = temp.path().join("kl.toml");
        std::fs::write(&config, "[agent]\nreview_timeout_secs = 1\n").unwrap();
        let home = tempfile::tempdir().unwrap();
        let (mut core, _buffer) = new_test_core(config, home.path());
        core.handshaken = true;
        core.config.agent.review_timeout_secs = 1;
        core.router = ProviderRouter::new(
            vec![(
                ModelMetadata::new(
                    "blocking-review",
                    "blocking-review",
                    Protocol::Scripted,
                    "test://blocking-review",
                    8192,
                    0.8,
                    4096,
                ),
                Arc::new(BlockingReviewProvider {
                    started: Arc::new(AtomicBool::new(false)),
                }) as Arc<dyn Provider>,
            )],
            "blocking-review",
            "blocking-review",
        );
        let started = Instant::now();
        let result = core
            .review_run(json!({
                "workspace": temp.path().to_string_lossy(),
                "mode": "strict"
            }))
            .unwrap();
        assert!(started.elapsed() < Duration::from_secs(3));
        let findings = result["findings"].as_array().unwrap();
        assert!(findings.iter().any(|finding| {
            finding["rule"] == "llm-review-unavailable"
                && finding["evidence"]
                    .as_str()
                    .is_some_and(|text| text.contains("timed out"))
        }));
    }
}
