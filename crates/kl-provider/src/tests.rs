use super::*;
use std::io::Cursor;

#[test]
fn parses_model_ids_from_common_gateway_shapes() {
    let openai = serde_json::json!({
        "data": [{"id": "gpt-5"}, {"id": "gpt-5"}, {"id": " "}]
    });
    assert_eq!(parse_model_ids(&openai), vec!["gpt-5"]);

    let gateway = serde_json::json!({
        "models": ["deepseek-chat", {"id": "deepseek-reasoner"}, {"name": "other"}]
    });
    assert_eq!(
        parse_model_ids(&gateway),
        vec!["deepseek-chat", "deepseek-reasoner", "other"]
    );
    assert!(parse_model_ids(&serde_json::json!({"object": "list"})).is_empty());
}

struct TestAdapter(Protocol);

impl ProtocolAdapter for TestAdapter {
    fn protocol(&self) -> Protocol {
        self.0
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        _cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        Err(ProviderError::invalid("test adapter is not called"))
    }
}

fn route(
    provider: &str,
    model: &str,
    protocol: Protocol,
    url: &str,
) -> (ModelMetadata, Arc<dyn Provider>) {
    let metadata = ModelMetadata::new(provider, model, protocol, url, 1000, 0.5, 100);
    let bound = BoundProvider::new(metadata.clone(), Arc::new(TestAdapter(protocol)));
    (metadata, Arc::new(bound) as Arc<dyn Provider>)
}

#[test]
fn sse_joins_multiline_data() {
    let stream = "event: message\n: comment\ndata: {\"a\":\ndata: 1}\n\n";
    let mut payloads = Vec::new();
    read_sse(
        Cursor::new(stream),
        &CancellationToken::new(),
        &mut |data| {
            payloads.push(data.to_owned());
            Ok(())
        },
    )
    .unwrap();
    assert_eq!(payloads, vec!["{\"a\":\n1}"]);
}

#[test]
fn read_sse_checks_cancellation() {
    let token = CancellationToken::new();
    token.cancel();
    let result = read_sse(Cursor::new("data: {}\n\n"), &token, &mut |_| Ok(()));
    assert_eq!(result.unwrap_err().kind, ProviderErrorKind::Cancelled);
}

#[test]
fn chat_message_text_concatenates_parts() {
    let message = ChatMessage::assistant_with_text("hello", Vec::new());
    assert_eq!(message.text(), "hello");
}

#[test]
fn routes_by_provider_and_model_to_bound_protocol() {
    let router = ProviderRouter::new(
        vec![
            route(
                "opencode",
                "gpt-5-chat",
                Protocol::OpenAiChat,
                "https://gateway.example/v1/chat/completions",
            ),
            route(
                "opencode",
                "claude-native",
                Protocol::Anthropic,
                "https://gateway.example/v1/messages",
            ),
            route(
                "opencode",
                "gpt-5-responses",
                Protocol::OpenAiResponses,
                "https://gateway.example/custom/responses",
            ),
        ],
        "opencode",
        "gpt-5-responses",
    );

    let (provider, metadata) = router
        .get(Some("opencode"), Some("gpt-5-responses"))
        .unwrap();
    assert_eq!(provider.kind(), "openai-responses");
    assert_eq!(metadata.protocol, Protocol::OpenAiResponses);
    assert_eq!(metadata.url, "https://gateway.example/custom/responses");
    assert_eq!(router.list().len(), 3);
}

#[test]
fn disabled_models_are_listed_but_not_selectable() {
    let (_, disabled_provider) = route(
        "opencode",
        "gpt-5-chat",
        Protocol::OpenAiChat,
        "https://gateway.example/v1/chat/completions",
    );
    let disabled_metadata = ModelMetadata::new(
        "opencode",
        "gpt-5-chat",
        Protocol::OpenAiChat,
        "https://gateway.example/v1/chat/completions",
        1000,
        0.5,
        100,
    )
    .with_enabled(false);
    let router = ProviderRouter::new(
        vec![
            (disabled_metadata, disabled_provider),
            route(
                "opencode",
                "claude-native",
                Protocol::Anthropic,
                "https://gateway.example/v1/messages",
            ),
        ],
        "opencode",
        "gpt-5-chat",
    );

    assert!(router.get(Some("opencode"), Some("gpt-5-chat")).is_err());
    let (_, metadata) = router.get(Some("opencode"), None).unwrap();
    assert_eq!(metadata.model, "claude-native");
    let listed = router.list();
    assert!(
        !listed
            .iter()
            .find(|model| model.model == "gpt-5-chat")
            .unwrap()
            .enabled
    );
}

#[test]
fn chat_bodies_include_reasoning_only_when_set() {
    let mut request = ChatRequest::new("model", vec![ChatMessage::user("hi")]);
    assert!(
        crate::openai::openai_body(&request)
            .get("reasoning_effort")
            .is_none()
    );
    assert!(
        crate::responses::responses_body(&request)
            .get("reasoning")
            .is_none()
    );

    request.reasoning_effort = Some("high".to_owned());
    assert_eq!(
        crate::openai::openai_body(&request)["reasoning_effort"],
        "high"
    );
    assert_eq!(
        crate::responses::responses_body(&request)["reasoning"]["effort"],
        "high"
    );
}

#[test]
fn disabled_provider_has_no_routes_and_reports_a_config_error() {
    let router = ProviderRouter::new(
        vec![route(
            "opencode",
            "gpt-5-chat",
            Protocol::OpenAiChat,
            "https://gateway.example/v1/chat/completions",
        )],
        "opencode",
        "gpt-5-chat",
    );
    router.disable_provider("opencode");

    assert!(router.list().is_empty());
    let error = match router.get(Some("opencode"), Some("gpt-5-chat")) {
        Ok(_) => panic!("disabled provider route unexpectedly resolved"),
        Err(error) => error,
    };
    assert_eq!(error.kind, ProviderErrorKind::Config);
    assert!(error.message.contains("disabled"));
}

#[test]
fn protocol_names_are_stable() {
    assert_eq!(Protocol::OpenAiChat.as_str(), "openai-chat");
    assert_eq!(Protocol::Anthropic.as_str(), "anthropic");
    assert_eq!(Protocol::OpenAiResponses.as_str(), "openai-responses");
}
