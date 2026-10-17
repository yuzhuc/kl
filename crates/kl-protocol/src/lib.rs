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
    pub const SESSION_CREATE: &str = "session.create";
    pub const SESSION_RESUME: &str = "session.resume";
    pub const SESSION_LIST: &str = "session.list";
    pub const TURN_START: &str = "turn.start";
    pub const TURN_CANCEL: &str = "turn.cancel";
    pub const APPROVAL_RESPOND: &str = "tool.approval.respond";
    pub const PROVIDER_LIST: &str = "provider.list";
    pub const AUDIT_TAIL: &str = "audit.tail";
    pub const CONFIG_GET: &str = "config.get";
    pub const PLUGIN_LIST: &str = "plugin.list";
    pub const PLUGIN_INSTALL: &str = "plugin.install";
    pub const PLUGIN_RELOAD: &str = "plugin.reload";
    pub const PLUGIN_ENABLE: &str = "plugin.enable";
    pub const PLUGIN_DISABLE: &str = "plugin.disable";
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
    pub available: bool,
    pub detail: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ProviderListResult {
    pub active_provider: String,
    pub active_model: String,
    pub models: Vec<ProviderModelSummary>,
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
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionResumeParams {
    pub session_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SessionListResult {
    pub sessions: Vec<SessionInfo>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TurnStartParams {
    pub session_id: String,
    pub prompt: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub provider: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub model: Option<String>,
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

/// Events emitted by the core and persisted to the session event log.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "type", content = "data")]
pub enum CoreEvent {
    #[serde(rename = "session.created")]
    SessionCreated { session_id: String },
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
}
