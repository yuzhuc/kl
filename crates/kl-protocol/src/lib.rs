//! KL wire protocol: JSON-RPC 2.0 envelopes, version handshake, stable error
//! codes, shared request/response payloads and core events.
//!
//! The protocol is deliberately small. `C#` and Rust both consume the same
//! fixtures in `tests/fixtures/protocol`, and the JSON Schema lives in
//! `schemas/protocol/v1`.

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

/// Protocol version implemented by this crate.
pub const PROTOCOL_VERSION: u32 = 1;
/// Oldest protocol version this build can still speak.
pub const MIN_PROTOCOL_VERSION: u32 = 1;
/// Canonical core process name.
pub const CORE_NAME: &str = "kl-core";
/// Core version compiled into the binary.
pub const CORE_VERSION: &str = env!("CARGO_PKG_VERSION");

fn current_protocol_version() -> u32 {
    PROTOCOL_VERSION
}

pub mod methods {
    pub const HANDSHAKE: &str = "kl.handshake";
    pub const PING: &str = "kl.ping";
    pub const DOCTOR: &str = "kl.doctor";
    pub const SERVER_INFO: &str = "server.info";
    pub const SESSION_CREATE: &str = "session.create";
    pub const SESSION_RESUME: &str = "session.resume";
    pub const SESSION_HISTORY: &str = "session.history";
    pub const SESSION_LIST: &str = "session.list";
    pub const SESSION_RENAME: &str = "session.rename";
    pub const SESSION_DELETE: &str = "session.delete";
    pub const SESSION_STATUS: &str = "session.status";
    pub const SESSION_COMPACT: &str = "session.compact";
    pub const SESSION_FORK: &str = "session.fork";
    pub const SESSION_ATTACH: &str = "session.attach";
    pub const SESSION_DETACH: &str = "session.detach";
    pub const SESSION_LEASE: &str = "session.lease";
    pub const TURN_START: &str = "turn.start";
    pub const TURN_CANCEL: &str = "turn.cancel";
    pub const APPROVAL_RESPOND: &str = "tool.approval.respond";
    pub const PERMISSIONS_GET: &str = "permissions.get";
    pub const PERMISSIONS_SET: &str = "permissions.set";
    pub const PROVIDER_LIST: &str = "provider.list";
    pub const PROVIDER_UPSERT: &str = "provider.upsert";
    pub const PROVIDER_SET_API_KEY: &str = "provider.set_api_key";
    pub const PROVIDER_SET_ENABLED: &str = "provider.set_enabled";
    pub const CONFIG_SET_DEFAULT_MODEL: &str = "config.set_default_model";
    pub const AUDIT_TAIL: &str = "audit.tail";
    pub const REVIEW_RUN: &str = "review.run";
    pub const CONFIG_GET: &str = "config.get";
    pub const WORKSPACE_INIT: &str = "workspace.init";
    pub const AGENT_CONFIG_GET: &str = "agent.config.get";
    pub const AGENT_CONFIG_SET: &str = "agent.config.set";
    pub const SKILL_LIST: &str = "skill.list";
    pub const SKILL_GET: &str = "skill.get";
    pub const MCP_LIST: &str = "mcp.list";
    pub const AGENT_LIST: &str = "agent.list";
    pub const PLUGIN_LIST: &str = "plugin.list";
    pub const PLUGIN_INSTALL: &str = "plugin.install";
    pub const PLUGIN_RELOAD: &str = "plugin.reload";
    pub const PLUGIN_ENABLE: &str = "plugin.enable";
    pub const PLUGIN_DISABLE: &str = "plugin.disable";
    pub const MEMORY_LIST: &str = "memory.list";
    pub const MEMORY_READ: &str = "memory.read";
    pub const MEMORY_WRITE: &str = "memory.write";
    pub const MEMORY_DELETE: &str = "memory.delete";
    pub const EVENT: &str = "event";
}

pub mod error_codes {
    pub const PARSE_ERROR: i64 = -32700;
    pub const INVALID_REQUEST: i64 = -32600;
    pub const METHOD_NOT_FOUND: i64 = -32601;
    pub const INVALID_PARAMS: i64 = -32602;
    pub const INTERNAL_ERROR: i64 = -32603;

    pub const VERSION_MISMATCH: i64 = -32001;
    pub const NOT_INITIALIZED: i64 = -32002;
    pub const SESSION_NOT_FOUND: i64 = -32003;
    pub const TURN_IN_PROGRESS: i64 = -32004;
    pub const PROVIDER_UNAVAILABLE: i64 = -32005;
    pub const APPROVAL_NOT_FOUND: i64 = -32006;
    pub const POLICY_DENIED: i64 = -32007;
    pub const SESSION_BUSY: i64 = -32008;
    pub const LEASE_REQUIRED: i64 = -32009;
    pub const PLUGIN_ERROR: i64 = -32010;
    pub const PLUGIN_NOT_FOUND: i64 = -32011;
    pub const PLUGIN_PERMISSION: i64 = -32012;
}

/// JSON-RPC request identifier. Both integer and string ids are legal.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(untagged)]
pub enum RequestId {
    Number(i64),
    String(String),
}

impl RequestId {
    pub fn as_value(&self) -> Value {
        match self {
            Self::Number(value) => json!(value),
            Self::String(value) => json!(value),
        }
    }
}

impl From<i64> for RequestId {
    fn from(value: i64) -> Self {
        Self::Number(value)
    }
}

impl From<&str> for RequestId {
    fn from(value: &str) -> Self {
        Self::String(value.to_owned())
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct RpcError {
    pub code: i64,
    pub message: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub data: Option<Value>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct IncomingMessage {
    pub jsonrpc: String,
    #[serde(default, rename = "kl_protocol")]
    pub kl_protocol: Option<u32>,
    #[serde(default)]
    pub id: Option<RequestId>,
    #[serde(default)]
    pub method: Option<String>,
    #[serde(default)]
    pub params: Value,
}

impl IncomingMessage {
    pub fn is_request(&self) -> bool {
        self.method.is_some() && self.id.is_some()
    }
}

pub fn validate_envelope(message: &IncomingMessage) -> Result<(), RpcError> {
    if message.jsonrpc != "2.0" {
        return Err(RpcError {
            code: error_codes::INVALID_REQUEST,
            message: "jsonrpc must be \"2.0\"".to_owned(),
            data: None,
        });
    }
    if message.method.is_none() {
        return Err(RpcError {
            code: error_codes::INVALID_REQUEST,
            message: "missing method".to_owned(),
            data: None,
        });
    }
    if let Some(version) = message.kl_protocol {
        if version != PROTOCOL_VERSION {
            return Err(RpcError {
                code: error_codes::VERSION_MISMATCH,
                message: format!("unsupported wire protocol version: {version}"),
                data: None,
            });
        }
    }
    Ok(())
}

pub fn success(id: RequestId, result: Value) -> Value {
    json!({
        "jsonrpc": "2.0",
        "kl_protocol": PROTOCOL_VERSION,
        "id": id,
        "result": result
    })
}

pub fn failure(id: Option<RequestId>, code: i64, message: impl Into<String>) -> Value {
    json!({
        "jsonrpc": "2.0",
        "kl_protocol": PROTOCOL_VERSION,
        "id": id,
        "error": { "code": code, "message": message.into() }
    })
}

pub fn failure_with_data(
    id: Option<RequestId>,
    code: i64,
    message: impl Into<String>,
    data: Value,
) -> Value {
    json!({
        "jsonrpc": "2.0",
        "kl_protocol": PROTOCOL_VERSION,
        "id": id,
        "error": { "code": code, "message": message.into(), "data": data }
    })
}

pub fn notification(method: &str, params: Value) -> Value {
    json!({
        "jsonrpc": "2.0",
        "kl_protocol": PROTOCOL_VERSION,
        "method": method,
        "params": params
    })
}

/// Pick the highest mutually supported protocol version.
pub fn negotiate(client_versions: &[u32]) -> Option<u32> {
    client_versions
        .iter()
        .copied()
        .filter(|version| (MIN_PROTOCOL_VERSION..=PROTOCOL_VERSION).contains(version))
        .max()
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct HandshakeParams {
    pub client_name: String,
    pub client_version: String,
    pub protocol_versions: Vec<u32>,
    #[serde(default)]
    pub features: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct HandshakeResult {
    pub server_name: String,
    pub server_version: String,
    pub protocol_version: u32,
    pub min_protocol_version: u32,
    pub features: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct PingParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub nonce: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PingResult {
    pub pong: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub nonce: Option<String>,
    pub server_time_unix_ms: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct DoctorCheck {
    pub name: String,
    pub ok: bool,
    pub detail: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct DoctorResult {
    pub ok: bool,
    pub core_version: String,
    pub protocol_version: u32,
    pub checks: Vec<DoctorCheck>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderModelSummary {
    pub provider: String,
    pub model: String,
    pub protocol: String,
    pub url: String,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    pub enabled: bool,
    pub key_configured: bool,
    pub available: bool,
    pub detail: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderListResult {
    pub active_provider: String,
    pub active_model: String,
    pub models: Vec<ProviderModelSummary>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct ProviderProtocolUrls {
    #[serde(
        rename = "openai-chat",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub openai_chat: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub anthropic: Option<String>,
    #[serde(
        rename = "openai-responses",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub openai_responses: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderModelInput {
    pub id: String,
    pub protocol: String,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderUpsertParams {
    pub id: String,
    #[serde(default)]
    pub protocols: ProviderProtocolUrls,
    pub models: Vec<ProviderModelInput>,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub api_key_env: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderUpsertResult {
    pub provider: String,
    pub path: String,
    pub models: usize,
    pub enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderSetApiKeyParams {
    pub id: String,
    pub key: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderKeyResult {
    pub provider: String,
    pub key_configured: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderSetEnabledParams {
    pub id: String,
    pub enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderEnabledResult {
    pub provider: String,
    pub enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SetDefaultModelParams {
    pub provider: String,
    pub model: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct DefaultModelResult {
    pub provider: String,
    pub model: String,
}

#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum ApprovalMode {
    #[default]
    Ask,
    Plan,
    #[serde(rename = "acceptEdits")]
    AcceptEdits,
    #[serde(rename = "dontAsk")]
    DontAsk,
    Auto,
    ReadOnly,
}

impl ApprovalMode {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Ask => "ask",
            Self::Plan => "plan",
            Self::AcceptEdits => "acceptEdits",
            Self::DontAsk => "dontAsk",
            Self::Auto => "auto",
            Self::ReadOnly => "read-only",
        }
    }
}

#[derive(Debug, Clone, Copy, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ReasoningEffort {
    #[default]
    Default,
    Low,
    Medium,
    High,
}

impl ReasoningEffort {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Default => "default",
            Self::Low => "low",
            Self::Medium => "medium",
            Self::High => "high",
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct SessionStatusParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SandboxCapabilitiesSummary {
    pub os_isolation: bool,
    pub filesystem_read_restriction: bool,
    pub filesystem_write_restriction: bool,
    pub child_process_containment: bool,
    pub network_isolation: bool,
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SandboxSummary {
    pub backend: String,
    pub strict: bool,
    pub capabilities: SandboxCapabilitiesSummary,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionStatusResult {
    pub session_id: String,
    pub workspace: String,
    pub provider: String,
    pub model: String,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    pub turn_active: bool,
    pub approval_mode: ApprovalMode,
    pub sandbox: SandboxSummary,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PermissionsResult {
    pub approval_mode: ApprovalMode,
    pub policy_summary: String,
    pub sandbox: SandboxSummary,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PermissionsSetParams {
    pub approval_mode: ApprovalMode,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct SessionCompactParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionCompactResult {
    pub omitted_messages: usize,
    pub estimated_tokens: u64,
    pub context_window: u32,
    pub compact_threshold: f32,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct WorkspaceInitParams {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default)]
    pub force: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct WorkspaceInitResult {
    pub path: String,
    pub created: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AgentConfigResult {
    pub reasoning_effort: ReasoningEffort,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AgentConfigSetParams {
    pub reasoning_effort: ReasoningEffort,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionCreateParams {
    pub workspace: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionInfo {
    pub session_id: String,
    pub workspace: String,
    pub provider: String,
    pub model: String,
    pub created_at: String,
    pub updated_at: String,
    pub message_count: usize,
    #[serde(default = "default_session_turn_state")]
    pub turn_state: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub status: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub task: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub prompt: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last_message: Option<String>,
    #[serde(default)]
    pub reasoning_effort: String,
}

fn default_session_turn_state() -> String {
    "idle".to_owned()
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionResumeParams {
    pub session_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionRenameParams {
    pub session_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionHistoryParams {
    pub session_id: String,
    #[serde(default = "default_history_limit")]
    pub limit: usize,
}

fn default_history_limit() -> usize {
    20
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionHistoryToolSummary {
    pub name: String,
    pub ok: bool,
    pub summary: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionHistoryEntry {
    pub role: String,
    pub text: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timestamp: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seq: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool: Option<SessionHistoryToolSummary>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionHistoryResult {
    pub session_id: String,
    pub messages: Vec<SessionHistoryEntry>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionListResult {
    pub sessions: Vec<SessionInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionDeleteParams {
    pub session_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionDeleteResult {
    pub deleted: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillListParams {
    #[serde(default)]
    pub session_id: Option<String>,
    #[serde(default)]
    pub workspace: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillInfo {
    pub name: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillListResult {
    pub skills: Vec<SkillInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillGetParams {
    pub session_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct SkillGetResult {
    pub name: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct McpToolInfo {
    pub name: String,
    pub description: String,
    pub source: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct McpListResult {
    pub tools: Vec<McpToolInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct AgentInfo {
    pub id: String,
    pub name: String,
    pub description: String,
    /// Tool access ceiling: `full`, `read-only` or `verify-only`.
    pub policy: String,
    /// `builtin` for the built-in presets, otherwise the source file name.
    pub source: String,
    pub built_in: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct AgentListResult {
    pub agents: Vec<AgentInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TurnStartParams {
    pub session_id: String,
    pub prompt: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub quality: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub agent: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TurnStartResult {
    pub turn_id: String,
    pub provider: String,
    pub model: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TurnCancelParams {
    pub session_id: String,
    pub turn_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TurnCancelResult {
    pub cancelled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ApprovalRespondParams {
    pub session_id: String,
    pub approval_id: String,
    pub decision: ApprovalDecision,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ApprovalDecision {
    Allow,
    AllowSession,
    Deny,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ApprovalRespondResult {
    pub accepted: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PluginInstallParams {
    pub path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PluginIdParams {
    pub id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PluginListResult {
    pub plugins: Vec<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AuditTailParams {
    #[serde(default = "default_audit_limit")]
    pub limit: usize,
}

fn default_audit_limit() -> usize {
    20
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AuditTailResult {
    pub records: Vec<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReviewRunParams {
    pub workspace: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub base: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mode: Option<String>,
    #[serde(default)]
    pub strict: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReviewFinding {
    pub rule: String,
    pub source: String,
    pub file: String,
    pub line: u32,
    pub severity: String,
    pub confidence: f32,
    pub evidence: String,
    pub recommendation: String,
    pub introduced: bool,
    pub low_confidence: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReviewResult {
    pub mode: String,
    pub summary: String,
    pub changed_files: usize,
    pub findings: Vec<ReviewFinding>,
}

/// Events emitted by the core and persisted to the session event log.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "type", content = "data")]
pub enum CoreEvent {
    #[serde(rename = "session.created")]
    SessionCreated { session_id: String },
    #[serde(rename = "user.message")]
    UserMessage { turn_id: String, text: String },
    #[serde(rename = "turn.started")]
    TurnStarted { turn_id: String },
    #[serde(rename = "turn.delta")]
    TurnDelta { turn_id: String, text: String },
    #[serde(rename = "turn.reasoning")]
    TurnReasoning { turn_id: String, text: String },
    #[serde(rename = "tool.started")]
    ToolStarted {
        turn_id: String,
        call_id: String,
        name: String,
        args: Value,
    },
    #[serde(rename = "tool.output")]
    ToolOutput {
        turn_id: String,
        call_id: String,
        stream: String,
        chunk: String,
    },
    #[serde(rename = "tool.approval.requested")]
    ToolApprovalRequested {
        turn_id: String,
        call_id: String,
        approval_id: String,
        name: String,
        action: String,
        resource: String,
        args: Value,
        reason: String,
    },
    #[serde(rename = "tool.approval.resolved")]
    ToolApprovalResolved {
        turn_id: String,
        call_id: String,
        approval_id: String,
        decision: ApprovalDecision,
    },
    #[serde(rename = "tool.completed")]
    ToolCompleted {
        turn_id: String,
        call_id: String,
        name: String,
        ok: bool,
        summary: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        output: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        diff: Option<String>,
    },
    #[serde(rename = "sandbox.violation")]
    SandboxViolation {
        turn_id: String,
        call_id: String,
        kind: String,
        resource: String,
        message: String,
    },
    #[serde(rename = "audit.recorded")]
    AuditRecorded {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        turn_id: Option<String>,
        record_id: String,
    },
    #[serde(rename = "turn.completed")]
    TurnCompleted {
        turn_id: String,
        status: String,
        text: String,
        #[serde(default)]
        input_tokens: u64,
        #[serde(default)]
        output_tokens: u64,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        cost_usd: Option<f64>,
    },
    #[serde(rename = "turn.failed")]
    TurnFailed { turn_id: String, message: String },
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct EventParams {
    pub session_id: String,
    pub seq: u64,
    pub event: CoreEvent,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct EventNotification {
    pub jsonrpc: String,
    #[serde(default = "current_protocol_version")]
    pub kl_protocol: u32,
    pub method: String,
    pub params: EventParams,
}

impl EventNotification {
    pub fn new(session_id: impl Into<String>, seq: u64, event: CoreEvent) -> Self {
        Self {
            jsonrpc: "2.0".to_owned(),
            kl_protocol: PROTOCOL_VERSION,
            method: methods::EVENT.to_owned(),
            params: EventParams {
                session_id: session_id.into(),
                seq,
                event,
            },
        }
    }
}

/// Identity and transport metadata of a running core.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ServerInfoResult {
    pub server_name: String,
    pub server_version: String,
    pub protocol_version: u32,
    pub transport: String,
    pub endpoint: String,
    pub pid: u32,
    pub started_at_unix_ms: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionAttachParams {
    pub session_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub after_seq: Option<u64>,
}

/// Replayed events reuse the notification payload so a client can replay
/// them through the same handler it uses for live `event` notifications.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionAttachResult {
    pub session_id: String,
    pub replay: Vec<EventParams>,
    pub last_seq: u64,
    pub lease_held: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionDetachParams {
    pub session_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionDetachResult {
    pub session_id: String,
    pub detached: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionLeaseParams {
    pub session_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub lease_ms: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionLeaseResult {
    pub session_id: String,
    pub expires_at_unix_ms: i64,
}

/// A single Markdown memory entry.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryEntryInfo {
    pub name: String,
    pub scope: String,
    pub size_bytes: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryListParams {
    pub session_id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryListResult {
    pub entries: Vec<MemoryEntryInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryReadParams {
    pub session_id: String,
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryReadResult {
    pub name: String,
    pub scope: String,
    pub content: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryWriteParams {
    pub session_id: String,
    pub name: String,
    pub content: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryWriteResult {
    pub name: String,
    pub scope: String,
    pub size_bytes: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryDeleteParams {
    pub session_id: String,
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub scope: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MemoryDeleteResult {
    pub name: String,
    pub scope: String,
    pub deleted: bool,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn negotiation_prefers_highest_common_version() {
        assert_eq!(negotiate(&[1]), Some(1));
        assert_eq!(negotiate(&[0, 1, 99]), Some(1));
        assert_eq!(negotiate(&[]), None);
        assert_eq!(negotiate(&[99]), None);
    }

    #[test]
    fn user_message_event_round_trips() {
        let event = CoreEvent::UserMessage {
            turn_id: "turn-1".to_owned(),
            text: "fix it".to_owned(),
        };
        let encoded = serde_json::to_value(&event).unwrap();
        assert_eq!(encoded["type"], "user.message");
        let decoded: CoreEvent = serde_json::from_value(encoded).unwrap();
        assert_eq!(decoded, event);
    }

    #[test]
    fn turn_start_agent_is_optional_and_round_trips() {
        let legacy: TurnStartParams = serde_json::from_value(serde_json::json!({
            "session_id": "session-1",
            "prompt": "fix it"
        }))
        .unwrap();
        assert_eq!(legacy.agent, None);

        let params = TurnStartParams {
            session_id: "session-1".to_owned(),
            prompt: "fix it".to_owned(),
            provider: None,
            model: None,
            quality: None,
            agent: Some("plan".to_owned()),
        };
        let encoded = serde_json::to_value(&params).unwrap();
        assert_eq!(encoded["agent"], "plan");
        let decoded: TurnStartParams = serde_json::from_value(encoded).unwrap();
        assert_eq!(decoded, params);
    }

    #[test]
    fn event_round_trips() {
        let event = CoreEvent::ToolCompleted {
            turn_id: "turn-1".to_owned(),
            call_id: "call-1".to_owned(),
            name: "read_file".to_owned(),
            ok: true,
            summary: "read 3 lines".to_owned(),
            output: None,
            diff: None,
        };
        let encoded = serde_json::to_value(&event).unwrap();
        assert_eq!(encoded["type"], "tool.completed");
        let decoded: CoreEvent = serde_json::from_value(encoded).unwrap();
        assert_eq!(decoded, event);
    }

    fn fixture(name: &str) -> serde_json::Value {
        let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../tests/fixtures/protocol")
            .join(name);
        let text = std::fs::read_to_string(&path)
            .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()));
        serde_json::from_str(&text).expect("fixture must be valid JSON")
    }

    #[test]
    fn r3_methods_and_error_codes_are_stable() {
        assert_eq!(methods::SERVER_INFO, "server.info");
        assert_eq!(methods::SESSION_ATTACH, "session.attach");
        assert_eq!(methods::SESSION_DETACH, "session.detach");
        assert_eq!(methods::SESSION_LEASE, "session.lease");
        assert_eq!(error_codes::SESSION_BUSY, -32008);
        assert_eq!(error_codes::LEASE_REQUIRED, -32009);
    }

    #[test]
    fn session_attach_params_allow_a_missing_after_seq() {
        let params: SessionAttachParams =
            serde_json::from_value(json!({ "session_id": "session-1" })).unwrap();
        assert_eq!(params.session_id, "session-1");
        assert_eq!(params.after_seq, None);

        let params: SessionAttachParams =
            serde_json::from_value(json!({ "session_id": "session-1", "after_seq": 4 })).unwrap();
        assert_eq!(params.after_seq, Some(4));
    }

    #[test]
    fn session_attach_result_replays_event_records() {
        let result: SessionAttachResult = serde_json::from_value(json!({
            "session_id": "session-1",
            "replay": [
                {
                    "session_id": "session-1",
                    "seq": 3,
                    "event": { "type": "turn.started", "data": { "turn_id": "turn-1" } }
                }
            ],
            "last_seq": 3,
            "lease_held": true
        }))
        .unwrap();
        assert_eq!(result.replay.len(), 1);
        assert_eq!(result.replay[0].seq, 3);
        assert_eq!(
            result.replay[0].event,
            CoreEvent::TurnStarted {
                turn_id: "turn-1".to_owned()
            }
        );
        assert_eq!(result.last_seq, 3);
        assert!(result.lease_held);
    }

    #[test]
    fn shared_fixtures_match_the_r3_contracts() {
        let server_info: ServerInfoResult =
            serde_json::from_value(fixture("server_info_result.json")["result"].clone()).unwrap();
        assert_eq!(server_info.server_name, "kl-core");
        assert_eq!(server_info.protocol_version, PROTOCOL_VERSION);
        assert_eq!(server_info.transport, "stdio");

        let attach: SessionAttachResult =
            serde_json::from_value(fixture("session_attach_result.json")["result"].clone())
                .unwrap();
        assert_eq!(attach.session_id, "session-1");
        assert_eq!(attach.replay.len(), 1);
        assert_eq!(attach.last_seq, 3);
        assert!(attach.lease_held);

        let detach: SessionDetachResult =
            serde_json::from_value(fixture("session_detach_result.json")["result"].clone())
                .unwrap();
        assert!(detach.detached);

        let lease: SessionLeaseResult =
            serde_json::from_value(fixture("session_lease_result.json")["result"].clone()).unwrap();
        assert_eq!(lease.session_id, "session-1");
        assert_eq!(lease.expires_at_unix_ms, 1760000060000);
    }

    #[test]
    fn memory_methods_are_stable() {
        assert_eq!(methods::MEMORY_LIST, "memory.list");
        assert_eq!(methods::MEMORY_READ, "memory.read");
        assert_eq!(methods::MEMORY_WRITE, "memory.write");
        assert_eq!(methods::MEMORY_DELETE, "memory.delete");
    }

    #[test]
    fn memory_params_use_snake_case_and_optional_scope() {
        let params: MemoryListParams =
            serde_json::from_value(json!({ "session_id": "session-1" })).unwrap();
        assert_eq!(params.session_id, "session-1");
        assert_eq!(params.scope, None);

        let params: MemoryReadParams = serde_json::from_value(json!({
            "session_id": "session-1",
            "name": "notes.md",
            "scope": "user"
        }))
        .unwrap();
        assert_eq!(params.session_id, "session-1");
        assert_eq!(params.name, "notes.md");
        assert_eq!(params.scope.as_deref(), Some("user"));

        let params: MemoryWriteParams = serde_json::from_value(json!({
            "session_id": "session-1",
            "name": "notes.md",
            "content": "# hello"
        }))
        .unwrap();
        assert_eq!(params.session_id, "session-1");
        assert_eq!(params.content, "# hello");
        assert_eq!(params.scope, None);
    }

    #[test]
    fn shared_fixtures_match_the_memory_contracts() {
        let list: MemoryListResult =
            serde_json::from_value(fixture("memory_list_result.json")["result"].clone()).unwrap();
        assert_eq!(list.entries.len(), 1);
        assert_eq!(list.entries[0].name, "notes.md");
        assert_eq!(list.entries[0].scope, "workspace");
        assert_eq!(list.entries[0].size_bytes, 7);

        let read: MemoryReadResult =
            serde_json::from_value(fixture("memory_read_result.json")["result"].clone()).unwrap();
        assert_eq!(read.name, "notes.md");
        assert_eq!(read.scope, "workspace");
        assert_eq!(read.content, "# hello\n");

        let write: MemoryWriteResult =
            serde_json::from_value(fixture("memory_write_result.json")["result"].clone()).unwrap();
        assert_eq!(write.name, "notes.md");
        assert_eq!(write.scope, "workspace");
        assert_eq!(write.size_bytes, 7);

        let delete: MemoryDeleteResult =
            serde_json::from_value(fixture("memory_delete_result.json")["result"].clone()).unwrap();
        assert_eq!(delete.name, "notes.md");
        assert_eq!(delete.scope, "workspace");
        assert!(delete.deleted);
    }
}
