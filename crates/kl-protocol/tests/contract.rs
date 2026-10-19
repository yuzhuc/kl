//! Shared wire fixtures are consumed by both the Rust core and the .NET CLI
//! tests. A failure here means the two sides have drifted.

use kl_protocol::{
    AgentConfigResult, AgentConfigSetParams, ApprovalMode, CoreEvent, EventNotification,
    HandshakeParams, HandshakeResult, IncomingMessage, PermissionsResult, PermissionsSetParams,
    PingParams, PingResult, ProviderListResult, ReasoningEffort, RequestId, ReviewResult,
    SessionCompactParams, SessionCompactResult, SessionHistoryParams, SessionHistoryResult,
    SessionStatusParams, SessionStatusResult, WorkspaceInitParams, WorkspaceInitResult,
    error_codes, methods,
};
use serde_json::Value;
use std::path::PathBuf;

fn fixtures() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/protocol")
}

fn read(name: &str) -> Value {
    let path = fixtures().join(name);
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()));
    serde_json::from_str(&text).expect("fixture must be valid JSON")
}

#[test]
fn handshake_request_matches_shared_contract() {
    let value = read("handshake_request.json");
    let message: IncomingMessage = serde_json::from_value(value.clone()).unwrap();
    assert_eq!(message.jsonrpc, "2.0");
    assert_eq!(message.kl_protocol, Some(1));
    assert_eq!(message.id, Some(RequestId::Number(1)));
    assert_eq!(message.method.as_deref(), Some(methods::HANDSHAKE));

    let params: HandshakeParams = serde_json::from_value(message.params).unwrap();
    assert_eq!(params.client_name, "kl-cli");
    assert_eq!(params.protocol_versions, vec![1]);
    assert_eq!(
        serde_json::to_value(params).unwrap()["client_version"],
        value["params"]["client_version"]
    );
}

#[test]
fn handshake_response_matches_shared_contract() {
    let value = read("handshake_response.json");
    let result: HandshakeResult = serde_json::from_value(value["result"].clone()).unwrap();
    assert_eq!(result.server_name, "kl-core");
    assert_eq!(result.protocol_version, 1);
    assert_eq!(result.min_protocol_version, 1);
    assert!(result.features.iter().any(|feature| feature == "events"));
}

#[test]
fn ping_messages_match_shared_contract() {
    let request = read("ping_request.json");
    let message: IncomingMessage = serde_json::from_value(request).unwrap();
    assert_eq!(message.kl_protocol, Some(1));
    assert_eq!(message.method.as_deref(), Some(methods::PING));
    let params: PingParams = serde_json::from_value(message.params).unwrap();
    assert_eq!(params.nonce.as_deref(), Some("contract-test"));

    let response = read("ping_response.json");
    let result: PingResult = serde_json::from_value(response["result"].clone()).unwrap();
    assert!(result.pong);
    assert_eq!(result.nonce.as_deref(), Some("contract-test"));
}

#[test]
fn event_notification_matches_shared_contract() {
    let value = read("event_turn_started.json");
    let notification: EventNotification = serde_json::from_value(value.clone()).unwrap();
    assert_eq!(notification.method, methods::EVENT);
    assert_eq!(notification.kl_protocol, 1);
    assert_eq!(notification.params.seq, 1);
    assert_eq!(
        notification.params.event,
        CoreEvent::TurnStarted {
            turn_id: "turn-1".to_owned()
        }
    );
    assert_eq!(serde_json::to_value(notification).unwrap(), value);
}

#[test]
fn user_message_event_matches_shared_contract() {
    let value = read("event_user_message.json");
    let notification: EventNotification = serde_json::from_value(value.clone()).unwrap();
    assert_eq!(notification.method, methods::EVENT);
    assert_eq!(notification.kl_protocol, 1);
    assert_eq!(
        notification.params.event,
        CoreEvent::UserMessage {
            turn_id: "turn-1".to_owned(),
            text: "fix it".to_owned(),
        }
    );
    assert_eq!(serde_json::to_value(notification).unwrap(), value);
}

#[test]
fn provider_list_result_matches_shared_contract() {
    let value = read("provider_list_result.json");
    let result: ProviderListResult = serde_json::from_value(value["result"].clone()).unwrap();
    assert_eq!(result.active_provider, "opencode");
    assert_eq!(result.active_model, "gpt-5-responses");
    assert_eq!(result.models.len(), 3);
    assert_eq!(result.models[0].protocol, "openai-chat");
    assert!(result.models[0].enabled);
    assert!(result.models[0].key_configured);
    assert_eq!(result.models[1].protocol, "anthropic");
    assert_eq!(result.models[2].protocol, "openai-responses");
    assert_eq!(
        result.models[2].url,
        "https://gateway.example/custom/responses"
    );
}

#[test]
fn review_result_matches_shared_contract() {
    let value = read("review_result.json");
    let result: ReviewResult = serde_json::from_value(value["result"].clone()).unwrap();
    assert_eq!(result.mode, "strict");
    assert_eq!(result.changed_files, 1);
    assert_eq!(result.findings.len(), 2);
    assert_eq!(result.findings[0].rule, "secret-openai");
    assert_eq!(result.findings[0].file, "src/a.rs");
    assert_eq!(result.findings[0].line, 3);
    assert!(result.findings[1].low_confidence);
    assert!((result.findings[0].confidence - 0.99).abs() < 0.001);
}

#[test]
fn version_mismatch_error_uses_stable_code() {
    let value = read("error_version_mismatch.json");
    assert_eq!(value["error"]["code"], error_codes::VERSION_MISMATCH);
    assert_eq!(value["jsonrpc"], "2.0");
}

#[test]
fn session_history_messages_match_shared_contract() {
    let request = read("session_history_request.json");
    let message: IncomingMessage = serde_json::from_value(request).unwrap();
    assert_eq!(message.method.as_deref(), Some(methods::SESSION_HISTORY));
    let params: SessionHistoryParams = serde_json::from_value(message.params).unwrap();
    assert_eq!(params.session_id, "session-1");
    assert_eq!(params.limit, 20);

    let response = read("session_history_result.json");
    let result: SessionHistoryResult = serde_json::from_value(response["result"].clone()).unwrap();
    assert_eq!(result.session_id, "session-1");
    assert_eq!(result.messages.len(), 3);
    assert_eq!(result.messages[0].role, "user");
    assert_eq!(result.messages[0].text, "fix it");
    assert_eq!(result.messages[0].seq, Some(2));
    assert!(result.messages[0].timestamp.is_some());
    assert_eq!(result.messages[1].role, "tool");
    let tool = result.messages[1].tool.as_ref().unwrap();
    assert_eq!(tool.name, "shell");
    assert!(tool.ok);
    assert_eq!(tool.summary, "exit 0 in 5 ms");
    assert_eq!(result.messages[2].role, "assistant");
    assert_eq!(result.messages[2].text, "done");

    let serialized = serde_json::to_string(&response).unwrap();
    for forbidden in ["args", "output", "diff", "api_key"] {
        assert!(
            !serialized.contains(&format!("\"{forbidden}\"")),
            "history leaked {forbidden}: {serialized}"
        );
    }
}

#[test]
fn command_support_messages_match_shared_contract() {
    let status_request = read("session_status_request.json");
    let message: IncomingMessage = serde_json::from_value(status_request).unwrap();
    assert_eq!(message.method.as_deref(), Some(methods::SESSION_STATUS));
    let params: SessionStatusParams = serde_json::from_value(message.params).unwrap();
    assert_eq!(params.session_id.as_deref(), Some("session-1"));
    let status_result: SessionStatusResult =
        serde_json::from_value(read("session_status_result.json")["result"].clone()).unwrap();
    assert_eq!(status_result.provider, "opencode");
    assert_eq!(status_result.approval_mode, ApprovalMode::Ask);
    assert!(!status_result.turn_active);

    let get_request: IncomingMessage =
        serde_json::from_value(read("permissions_get_request.json")).unwrap();
    assert_eq!(
        get_request.method.as_deref(),
        Some(methods::PERMISSIONS_GET)
    );
    let permissions_result: PermissionsResult =
        serde_json::from_value(read("permissions_result.json")["result"].clone()).unwrap();
    assert_eq!(permissions_result.approval_mode, ApprovalMode::Auto);
    assert!(!permissions_result.sandbox.strict);

    let set_request: IncomingMessage =
        serde_json::from_value(read("permissions_set_request.json")).unwrap();
    assert_eq!(
        set_request.method.as_deref(),
        Some(methods::PERMISSIONS_SET)
    );
    let set_params: PermissionsSetParams = serde_json::from_value(set_request.params).unwrap();
    assert_eq!(set_params.approval_mode, ApprovalMode::Auto);
    let set_result: PermissionsResult =
        serde_json::from_value(read("permissions_set_result.json")["result"].clone()).unwrap();
    assert_eq!(set_result.approval_mode, ApprovalMode::Auto);

    let compact_request: IncomingMessage =
        serde_json::from_value(read("session_compact_request.json")).unwrap();
    assert_eq!(
        compact_request.method.as_deref(),
        Some(methods::SESSION_COMPACT)
    );
    let compact_params: SessionCompactParams =
        serde_json::from_value(compact_request.params).unwrap();
    assert_eq!(compact_params.session_id.as_deref(), Some("session-1"));
    let compact_result: SessionCompactResult =
        serde_json::from_value(read("session_compact_result.json")["result"].clone()).unwrap();
    assert_eq!(compact_result.omitted_messages, 6);
    assert!(compact_result.estimated_tokens > 0);

    let init_request: IncomingMessage =
        serde_json::from_value(read("workspace_init_request.json")).unwrap();
    assert_eq!(
        init_request.method.as_deref(),
        Some(methods::WORKSPACE_INIT)
    );
    let init_params: WorkspaceInitParams = serde_json::from_value(init_request.params).unwrap();
    assert_eq!(init_params.path.as_deref(), Some("."));
    let init_result: WorkspaceInitResult =
        serde_json::from_value(read("workspace_init_result.json")["result"].clone()).unwrap();
    assert!(init_result.created);

    let agent_get: IncomingMessage =
        serde_json::from_value(read("agent_config_get_request.json")).unwrap();
    assert_eq!(agent_get.method.as_deref(), Some(methods::AGENT_CONFIG_GET));
    let agent_result: AgentConfigResult =
        serde_json::from_value(read("agent_config_result.json")["result"].clone()).unwrap();
    assert_eq!(agent_result.reasoning_effort, ReasoningEffort::High);

    let agent_set: IncomingMessage =
        serde_json::from_value(read("agent_config_set_request.json")).unwrap();
    assert_eq!(agent_set.method.as_deref(), Some(methods::AGENT_CONFIG_SET));
    let agent_params: AgentConfigSetParams = serde_json::from_value(agent_set.params).unwrap();
    assert_eq!(agent_params.reasoning_effort, ReasoningEffort::High);
    let agent_set_result: AgentConfigResult =
        serde_json::from_value(read("agent_config_set_result.json")["result"].clone()).unwrap();
    assert_eq!(agent_set_result.reasoning_effort, ReasoningEffort::High);

    for value in [
        read("session_status_result.json"),
        read("permissions_result.json"),
        read("session_compact_result.json"),
        read("workspace_init_result.json"),
        read("agent_config_result.json"),
    ] {
        let serialized = value.to_string();
        assert!(!serialized.contains("api_key"));
        assert!(!serialized.contains("api_key_env"));
    }
}

#[test]
fn method_schema_is_valid_json_with_command_definitions() {
    let path = fixtures().join("../../../schemas/protocol/v1/methods.schema.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()));
    let schema: Value = serde_json::from_str(&text).unwrap();
    for definition in [
        "sessionStatusParams",
        "sessionStatusResult",
        "permissionsResult",
        "sessionCompactResult",
        "workspaceInitResult",
        "agentConfigResult",
    ] {
        assert!(
            schema["$defs"].get(definition).is_some(),
            "missing schema definition: {definition}"
        );
    }
}
