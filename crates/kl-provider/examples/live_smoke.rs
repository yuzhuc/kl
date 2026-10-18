use kl_provider::{
    AnthropicAdapter, CancellationToken, ChatEvent, ChatMessage, ChatRequest, OpenAiChatAdapter,
    OpenAiResponsesAdapter, ProtocolAdapter,
};
use serde_json::json;
use std::io::{Error, ErrorKind};
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut protocol = String::from("openai-chat");
    let mut model = None;
    let mut endpoint = None;
    let mut key_env = None;
    let mut output = None;
    let mut args = std::env::args().skip(1);
    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--protocol" => {
                protocol = args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--protocol requires a value")
                })?;
            }
            "--model" => {
                model = Some(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--model requires a value")
                })?);
            }
            "--url" => {
                endpoint = Some(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--url requires a value")
                })?);
            }
            "--key-env" => {
                key_env = Some(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--key-env requires a value")
                })?);
            }
            "--out" => {
                output = Some(PathBuf::from(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--out requires a value")
                })?));
            }
            other => {
                return Err(Error::new(
                    ErrorKind::InvalidInput,
                    format!("unknown argument: {other}"),
                )
                .into());
            }
        }
    }

    let (default_url, default_model, default_key_env) = match protocol.as_str() {
        "openai-chat" => (
            "https://api.openai.com/v1/chat/completions",
            "gpt-4o-mini",
            "OPENAI_API_KEY",
        ),
        "anthropic" => (
            "https://api.anthropic.com/v1/messages",
            "claude-sonnet-4-5",
            "ANTHROPIC_API_KEY",
        ),
        "openai-responses" => (
            "https://api.openai.com/v1/responses",
            "gpt-4o-mini",
            "OPENAI_API_KEY",
        ),
        other => {
            return Err(Error::new(
                ErrorKind::InvalidInput,
                format!("unknown protocol: {other}"),
            )
            .into());
        }
    };
    let key_env = key_env.unwrap_or_else(|| default_key_env.to_owned());
    let Some(api_key) = std::env::var(&key_env)
        .ok()
        .filter(|key| !key.trim().is_empty())
    else {
        println!("SKIP: {key_env} is not set");
        std::process::exit(2);
    };
    let endpoint = endpoint.unwrap_or_else(|| default_url.to_owned());
    let model = model.unwrap_or_else(|| default_model.to_owned());
    let output = output
        .unwrap_or_else(|| PathBuf::from(format!("tests/fixtures/providers/{protocol}-live.json")));

    let mut request = ChatRequest::new(
        model.clone(),
        vec![ChatMessage::user("Reply with exactly: KL_LIVE_OK")],
    );
    request.system = "You are a live smoke test. Follow the user's instruction exactly.".to_owned();
    request.max_tokens = 16;
    request.temperature = 0.0;

    let mut events = Vec::new();
    let mut record_event = |event: ChatEvent| {
        if let ChatEvent::TextDelta(text) = event {
            events.push(json!({"type": "text_delta", "text": text}));
        }
    };
    let outcome = match protocol.as_str() {
        "openai-chat" => OpenAiChatAdapter::new(endpoint.clone(), Some(api_key)).stream_chat(
            &request,
            &CancellationToken::new(),
            &mut record_event,
        )?,
        "anthropic" => AnthropicAdapter::new(endpoint.clone(), Some(api_key)).stream_chat(
            &request,
            &CancellationToken::new(),
            &mut record_event,
        )?,
        "openai-responses" => OpenAiResponsesAdapter::new(endpoint.clone(), Some(api_key))
            .stream_chat(&request, &CancellationToken::new(), &mut record_event)?,
        _ => unreachable!(),
    };

    let record = json!({
        "status": "ok",
        "protocol": protocol,
        "endpoint": endpoint,
        "model": model,
        "generated_at_unix_ms": SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_millis())
            .unwrap_or(0),
        "text": outcome.text,
        "finish_reason": outcome.finish_reason,
        "usage": outcome.usage,
        "events": events,
    });
    if let Some(parent) = output.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&output, serde_json::to_vec_pretty(&record)?)?;
    println!("wrote {}", output.display());
    Ok(())
}
