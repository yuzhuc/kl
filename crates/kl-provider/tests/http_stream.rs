use kl_provider::{
    AnthropicAdapter, CancellationToken, ChatEvent, ChatMessage, ChatRequest, FinishReason,
    OpenAiChatAdapter, OpenAiResponsesAdapter, ProtocolAdapter, ProviderErrorKind,
};
use std::io::{Read, Write};
use std::net::TcpListener;
use std::thread;
use std::time::{Duration, Instant};

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
fn openai_chat_uses_full_configured_url() {
    let body = concat!(
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n",
        "data: {\"choices\":[{\"delta\":{\"content\":\" world\"},\"finish_reason\":\"stop\"}],",
        "\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2}}\n\n",
        "data: [DONE]\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let url = format!("{base_url}/custom/chat?tenant=kl");
    let adapter = OpenAiChatAdapter::new(url, Some("test-key".to_owned()));
    let mut request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    request.system = "system".to_owned();

    let mut deltas = String::new();
    let outcome = adapter
        .stream_chat(&request, &CancellationToken::new(), &mut |event| {
            if let ChatEvent::TextDelta(text) = event {
                deltas.push_str(&text);
            }
        })
        .expect("OpenAI Chat stream");

    assert_eq!(deltas, "Hello world");
    assert_eq!(outcome.finish_reason, FinishReason::Stop);
    assert_eq!(outcome.usage.input_tokens, 4);
    let wire = server.join().expect("mock server").to_lowercase();
    assert!(
        wire.contains("post /custom/chat?tenant=kl http/1.1"),
        "{wire}"
    );
    assert!(!wire.contains("/chat/completions"), "{wire}");
}

#[test]
fn anthropic_uses_full_configured_url() {
    let body = concat!(
        "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n",
        "data: {\"type\":\"content_block_delta\",\"index\":0,",
        "\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n",
        "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},",
        "\"usage\":{\"output_tokens\":2}}\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let url = format!("{base_url}/custom/messages");
    let adapter = AnthropicAdapter::new(url, Some("test-key".to_owned()));
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let outcome = adapter
        .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
        .expect("Anthropic stream");
    assert_eq!(outcome.text, "Hi");
    let wire = server.join().expect("mock server").to_lowercase();
    assert!(wire.contains("post /custom/messages http/1.1"), "{wire}");
    assert!(!wire.contains("/v1/messages"), "{wire}");
}

#[test]
fn responses_uses_full_configured_url() {
    let body = concat!(
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n",
        "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let url = format!("{base_url}/custom/responses");
    let adapter = OpenAiResponsesAdapter::new(url, Some("test-key".to_owned()));
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let outcome = adapter
        .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
        .expect("Responses stream");
    assert_eq!(outcome.text, "Hi");
    let wire = server.join().expect("mock server").to_lowercase();
    assert!(wire.contains("post /custom/responses http/1.1"), "{wire}");
    assert!(!wire.contains("/v1/responses"), "{wire}");
}

#[test]
fn responses_tool_call_stream() {
    let body = concat!(
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"read_file\",\"arguments\":\"\"}}\n\n",
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"path\\\":\"}\n\n",
        "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"\\\"a.txt\\\"}\"}\n\n",
        "data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}\n\n",
        "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}}\n\n",
    );
    let (base_url, server) = serve_once(sse(body));
    let adapter = OpenAiResponsesAdapter::new(
        format!("{base_url}/custom/responses"),
        Some("test-key".to_owned()),
    );
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let outcome = adapter
        .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
        .expect("Responses tool stream");
    assert_eq!(outcome.finish_reason, FinishReason::ToolCalls);
    assert_eq!(outcome.tool_calls[0].name, "read_file");
    assert_eq!(outcome.tool_calls[0].arguments, "{\"path\":\"a.txt\"}");
    let _ = server.join().expect("mock server");
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
    let adapter = OpenAiChatAdapter::new(
        format!("{base_url}/custom/chat"),
        Some("bad-key".to_owned()),
    );
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let error = adapter
        .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
        .expect_err("auth error");
    assert_eq!(error.kind, ProviderErrorKind::Auth);
    assert_eq!(error.status, Some(401));
    assert!(error.message.contains("bad key"));
    let _ = server.join().expect("mock server");
}

fn serve_hold() -> (String, thread::JoinHandle<bool>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind mock server");
    let address = listener.local_addr().expect("mock address");
    let handle = thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("accept request");
        stream
            .set_read_timeout(Some(Duration::from_millis(100)))
            .expect("read timeout");
        let mut buffer = Vec::new();
        let mut chunk = [0u8; 4096];
        loop {
            let count = stream.read(&mut chunk).expect("read request");
            if count == 0 {
                return false;
            }
            buffer.extend_from_slice(&chunk[..count]);
            if buffer.windows(4).any(|window| window == b"\r\n\r\n") {
                break;
            }
        }
        stream
            .write_all(
                b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n",
            )
            .expect("write headers");
        stream.flush().expect("flush headers");
        let deadline = Instant::now() + Duration::from_secs(3);
        let mut byte = [0u8; 1];
        loop {
            match stream.read(&mut byte) {
                Ok(0) => return true,
                Ok(_) => {}
                Err(error)
                    if matches!(
                        error.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                    ) =>
                {
                    if Instant::now() >= deadline {
                        return false;
                    }
                }
                Err(_) => return false,
            }
        }
    });
    (format!("http://{address}"), handle)
}
#[test]
fn cancellation_interrupts_slow_http_stream() {
    let (base_url, server) = serve_hold();
    let adapter = OpenAiChatAdapter::new(
        format!("{base_url}/custom/chat"),
        Some("test-key".to_owned()),
    );
    let request = ChatRequest::new("test-model", vec![ChatMessage::user("hi")]);
    let token = CancellationToken::new();
    let cancel = token.clone();
    thread::spawn(move || {
        thread::sleep(Duration::from_millis(200));
        cancel.cancel();
    });

    let started = Instant::now();
    let error = adapter
        .stream_chat(&request, &token, &mut |_| {})
        .expect_err("slow stream should be cancelled");
    assert_eq!(error.kind, ProviderErrorKind::Cancelled);
    assert!(started.elapsed() < Duration::from_secs(2));
    assert!(server.join().expect("mock server"));
}
