use kl_provider::{
    AnthropicProvider, ChatEvent, ChatMessage, ChatRequest, FinishReason, OpenAiChatProvider,
    Provider, ProviderErrorKind,
};
use std::io::{Read, Write};
use std::net::TcpListener;
use std::thread;
use std::time::Duration;

fn serve_once(response: String) -> (String, thread::JoinHandle<String>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind mock server");
    let address = listener.local_addr().expect("mock address");
    let handle = thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("accept request");
        stream
            .set_read_timeout(Some(Duration::from_secs(5)))
            .expect("read timeout");
        let mut buffer = Vec::new();
        let mut chunk = [0u8; 4096];
        loop {
            let count = stream.read(&mut chunk).expect("read request");
            if count == 0 {
                break;
            }
            buffer.extend_from_slice(&chunk[..count]);
            let Some(header_end) = buffer.windows(4).position(|window| window == b"\r\n\r\n")
            else {
                continue;
            };
            let headers = String::from_utf8_lossy(&buffer[..header_end]).to_lowercase();
            let length = headers
                .lines()
                .find_map(|line| line.strip_prefix("content-length:"))
                .and_then(|value| value.trim().parse::<usize>().ok())
                .unwrap_or(0);
            let body_start = header_end + 4;
            while buffer.len().saturating_sub(body_start) < length {
                let count = stream.read(&mut chunk).expect("read body");
                if count == 0 {
                    break;
                }
                buffer.extend_from_slice(&chunk[..count]);
            }
            let request = String::from_utf8_lossy(&buffer).into_owned();
            stream
                .write_all(response.as_bytes())
                .expect("write response");
            stream.flush().expect("flush response");
            return request;
        }
        String::new()
    });
    (format!("http://{address}"), handle)
}

fn sse(body: &str) -> String {
    format!("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n{body}")
}

#[test]
fn openai_chat_streams_text_usage_and_auth() {
    let body = concat!(
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n",
        "data: {\"choices\":[{\"delta\":{\"content\":\" world\"},\"finish_reason\":\"stop\"}],",
        "\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2}}\n\n",
        "data: [DONE]\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let provider = OpenAiChatProvider::new(
        "openai",
        base_url,
        Some("test-key".to_owned()),
        vec!["test-model".to_owned()],
    );
    let mut request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    request.system = "system".to_owned();

    let mut deltas = String::new();
    let outcome = provider
        .stream_chat(&request, &mut |event| {
            if let ChatEvent::TextDelta(text) = event {
                deltas.push_str(&text);
            }
        })
        .expect("OpenAI stream");

    assert_eq!(deltas, "Hello world");
    assert_eq!(outcome.text, "Hello world");
    assert_eq!(outcome.finish_reason, FinishReason::Stop);
    assert_eq!(outcome.usage.input_tokens, 4);
    assert_eq!(outcome.usage.output_tokens, 2);

    let wire = server.join().expect("mock server");
    let wire = wire.to_lowercase();
    assert!(wire.contains("post /chat/completions http/1.1"), "{wire}");
    assert!(wire.contains("authorization: bearer test-key"), "{wire}");
    assert!(wire.contains("\"stream\":true"), "{wire}");
    assert!(
        wire.contains("{\"content\":\"system\",\"role\":\"system\"}"),
        "{wire}"
    );
}

#[test]
fn anthropic_messages_stream_text_and_headers() {
    let body = concat!(
        "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n",
        "data: {\"type\":\"content_block_delta\",\"index\":0,",
        "\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n",
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},",
        "\"usage\":{\"output_tokens\":2}}\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let provider = AnthropicProvider::new(
        "anthropic",
        base_url,
        Some("test-key".to_owned()),
        vec!["test-model".to_owned()],
    );
    let mut request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    request.system = "system".to_owned();

    let outcome = provider
        .stream_chat(&request, &mut |_| {})
        .expect("Anthropic stream");
    assert_eq!(outcome.text, "Hi");
    assert_eq!(outcome.finish_reason, FinishReason::Stop);
    assert_eq!(outcome.usage.input_tokens, 5);
    assert_eq!(outcome.usage.output_tokens, 2);

    let wire = server.join().expect("mock server");
    let wire = wire.to_lowercase();
    assert!(wire.contains("post /v1/messages http/1.1"), "{wire}");
    assert!(wire.contains("x-api-key: test-key"), "{wire}");
    assert!(wire.contains("anthropic-version: 2023-06-01"), "{wire}");
}

#[test]
fn openai_auth_error_is_normalized() {
    let response = concat!(
        "HTTP/1.1 401 Unauthorized\r\n",
        "Content-Type: application/json\r\n",
        "Connection: close\r\n\r\n",
        "{\"error\":{\"message\":\"bad key\"}}",
    );
    let (base_url, server) = serve_once(response.to_owned());
    let provider = OpenAiChatProvider::new(
        "openai",
        base_url,
        Some("bad-key".to_owned()),
        vec!["test-model".to_owned()],
    );
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let error = provider
        .stream_chat(&request, &mut |_| {})
        .expect_err("auth error");
    assert_eq!(error.kind, ProviderErrorKind::Auth);
    assert_eq!(error.status, Some(401));
    assert!(error.message.contains("bad key"));
    let _ = server.join().expect("mock server");
}
