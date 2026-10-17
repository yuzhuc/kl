//! Shared wire fixtures are consumed by both the Rust core and the .NET CLI
//! tests. A failure here means the two sides have drifted.

use kl_protocol::{
    CoreEvent, EventNotification, HandshakeParams, HandshakeResult, IncomingMessage, PingParams,
    PingResult, ProviderListResult, RequestId, error_codes, methods,
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
fn version_mismatch_error_uses_stable_code() {
    let value = read("error_version_mismatch.json");
    assert_eq!(value["error"]["code"], error_codes::VERSION_MISMATCH);
    assert_eq!(value["jsonrpc"], "2.0");
}
