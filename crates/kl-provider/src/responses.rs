use crate::{
    ChatEvent, ChatOutcome, ChatRequest, FinishReason, Protocol, ProtocolAdapter, ProviderError,
    ProviderErrorKind, Role, ToolCall, Usage, http_post_stream, read_sse,
};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader};
use std::sync::atomic::AtomicBool;

/// Native OpenAI Responses API adapter. The configured endpoint is used
/// exactly as the complete request URL.
pub struct OpenAiResponsesAdapter {
    endpoint: String,
    api_key: Option<String>,
}

impl OpenAiResponsesAdapter {
    pub fn new(endpoint: impl Into<String>, api_key: Option<String>) -> Self {
        Self {
            endpoint: endpoint.into(),
            api_key,
        }
    }

    pub fn parse_stream<R: BufRead>(
        &self,
        reader: R,
        cancel: &AtomicBool,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let mut text = String::new();
        let mut usage = Usage::default();
        let mut finish = FinishReason::Stop;
        let mut tool_calls: BTreeMap<usize, PartialToolCall> = BTreeMap::new();

        read_sse(reader, cancel, &mut |data| {
            if data.trim() == "[DONE]" {
                return Ok(());
            }
            let value: Value = serde_json::from_str(data).map_err(|error| {
                ProviderError::invalid(format!("bad Responses SSE JSON: {error}"))
            })?;
            let event_type = value
                .get("type")
                .and_then(Value::as_str)
                .unwrap_or_default();
            match event_type {
                "response.output_text.delta" => {
                    if let Some(delta) = value.get("delta").and_then(Value::as_str) {
                        if !delta.is_empty() {
                            text.push_str(delta);
                            on_event(ChatEvent::TextDelta(delta.to_owned()));
                        }
                    }
                }
                "response.output_text.done" => {
                    if text.is_empty() {
                        if let Some(done) = value.get("text").and_then(Value::as_str) {
                            if !done.is_empty() {
                                text.push_str(done);
                                on_event(ChatEvent::TextDelta(done.to_owned()));
                            }
                        }
                    }
                }
                "response.output_item.added" => {
                    if let Some(item) = value.get("item") {
                        if item.get("type").and_then(Value::as_str) == Some("function_call") {
                            let index = output_index(&value);
                            let partial = tool_calls.entry(index).or_default();
                            if let Some(id) = item.get("call_id").or_else(|| item.get("id")) {
                                if let Some(id) = id.as_str() {
                                    partial.id = id.to_owned();
                                }
                            }
                            if let Some(name) = item.get("name").and_then(Value::as_str) {
                                partial.name = name.to_owned();
                            }
                            if let Some(arguments) = item.get("arguments").and_then(Value::as_str) {
                                partial.arguments.push_str(arguments);
                            }
                            on_event(ChatEvent::ToolCallDelta {
                                index,
                                id: Some(partial.id.clone()),
                                name: Some(partial.name.clone()),
                                arguments: None,
                            });
                        }
                    }
                }
                "response.function_call_arguments.delta" => {
                    let index = output_index(&value);
                    if let Some(delta) = value.get("delta").and_then(Value::as_str) {
                        if !delta.is_empty() {
                            tool_calls
                                .entry(index)
                                .or_default()
                                .arguments
                                .push_str(delta);
                            on_event(ChatEvent::ToolCallDelta {
                                index,
                                id: None,
                                name: None,
                                arguments: Some(delta.to_owned()),
                            });
                        }
                    }
                }
                "response.function_call_arguments.done" => {
                    let index = output_index(&value);
                    if let Some(arguments) = value.get("arguments").and_then(Value::as_str) {
                        tool_calls.entry(index).or_default().arguments = arguments.to_owned();
                    }
                }
                "response.output_item.done" => {
                    if let Some(item) = value.get("item") {
                        if item.get("type").and_then(Value::as_str) == Some("function_call") {
                            let index = output_index(&value);
                            let partial = tool_calls.entry(index).or_default();
                            if let Some(id) = item.get("call_id").or_else(|| item.get("id")) {
                                if let Some(id) = id.as_str() {
                                    partial.id = id.to_owned();
                                }
                            }
                            if let Some(name) = item.get("name").and_then(Value::as_str) {
                                partial.name = name.to_owned();
                            }
                            if let Some(arguments) = item.get("arguments").and_then(Value::as_str) {
                                partial.arguments = arguments.to_owned();
                            }
                        }
                    }
                }
                "response.completed" => {
                    if let Some(response) = value.get("response") {
                        usage = responses_usage(response, usage);
                        on_event(ChatEvent::Usage(usage));
                    }
                    finish = FinishReason::Stop;
                }
                "response.incomplete" => {
                    finish = FinishReason::Length;
                    if let Some(response) = value.get("response") {
                        usage = responses_usage(response, usage);
                    }
                }
                "response.failed" => {
                    let message = value
                        .get("response")
                        .and_then(|response| response.get("error"))
                        .and_then(|error| error.get("message"))
                        .and_then(Value::as_str)
                        .unwrap_or("Responses API failed");
                    return Err(ProviderError::new(ProviderErrorKind::Server, message));
                }
                "error" => {
                    let message = value
                        .get("error")
                        .and_then(|error| error.get("message"))
                        .and_then(Value::as_str)
                        .or_else(|| value.get("message").and_then(Value::as_str))
                        .unwrap_or("Responses API stream error");
                    return Err(ProviderError::new(ProviderErrorKind::Server, message));
                }
                _ => {}
            }
            Ok(())
        })?;

        let tool_calls = tool_calls
            .into_values()
            .filter(|call| !call.name.is_empty())
            .map(|call| ToolCall {
                id: if call.id.is_empty() {
                    format!("call-{}", uuid_like())
                } else {
                    call.id
                },
                name: call.name,
                arguments: if call.arguments.is_empty() {
                    "{}".to_owned()
                } else {
                    call.arguments
                },
            })
            .collect::<Vec<_>>();
        if !tool_calls.is_empty() {
            finish = FinishReason::ToolCalls;
        }

        Ok(ChatOutcome {
            text,
            tool_calls,
            finish_reason: finish,
            usage,
        })
    }
}

impl ProtocolAdapter for OpenAiResponsesAdapter {
    fn protocol(&self) -> Protocol {
        Protocol::OpenAiResponses
    }

    fn available(&self) -> (bool, String) {
        match &self.api_key {
            Some(_) => (true, "API key present".to_owned()),
            None => (false, "API key environment variable is not set".to_owned()),
        }
    }

    fn stream_chat(
        &self,
        request: &ChatRequest,
        cancel: &AtomicBool,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let Some(api_key) = &self.api_key else {
            return Err(ProviderError::config(
                "OpenAI Responses API key is not configured",
            ));
        };
        let body = responses_body(request);
        let headers = [("authorization", format!("Bearer {api_key}"))];
        let reader = http_post_stream(&self.endpoint, &headers, &body)?;
        self.parse_stream(BufReader::new(reader), cancel, on_event)
    }
}

#[derive(Debug, Default)]
struct PartialToolCall {
    id: String,
    name: String,
    arguments: String,
}

fn output_index(value: &Value) -> usize {
    value
        .get("output_index")
        .or_else(|| value.get("item_id").and_then(|_| value.get("output_index")))
        .and_then(Value::as_u64)
        .unwrap_or(0) as usize
}

fn responses_usage(response: &Value, current: Usage) -> Usage {
    let Some(usage) = response.get("usage") else {
        return current;
    };
    Usage {
        input_tokens: usage
            .get("input_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(current.input_tokens),
        output_tokens: usage
            .get("output_tokens")
            .and_then(Value::as_u64)
            .unwrap_or(current.output_tokens),
    }
}

pub fn responses_body(request: &ChatRequest) -> Value {
    let mut input = Vec::new();
    if !request.system.is_empty() {
        input.push(json!({
            "type": "message",
            "role": "system",
            "content": [{"type": "input_text", "text": request.system}],
        }));
    }
    for message in &request.messages {
        match message.role {
            Role::System => input.push(json!({
                "type": "message",
                "role": "system",
                "content": [{"type": "input_text", "text": message.text()}],
            })),
            Role::User => input.push(json!({
                "type": "message",
                "role": "user",
                "content": [{"type": "input_text", "text": message.text()}],
            })),
            Role::Assistant => {
                if !message.text().is_empty() {
                    input.push(json!({
                        "type": "message",
                        "role": "assistant",
                        "content": [{"type": "output_text", "text": message.text()}],
                    }));
                }
                for call in &message.tool_calls {
                    input.push(json!({
                        "type": "function_call",
                        "call_id": call.id,
                        "name": call.name,
                        "arguments": call.arguments,
                    }));
                }
            }
            Role::Tool => input.push(json!({
                "type": "function_call_output",
                "call_id": message.tool_call_id,
                "output": message.text(),
            })),
        }
    }

    let mut body = json!({
        "model": request.model,
        "input": input,
        "stream": true,
        "max_output_tokens": request.max_tokens,
        "temperature": request.temperature,
    });
    if !request.tools.is_empty() {
        body["tools"] = Value::Array(
            request
                .tools
                .iter()
                .map(|tool| {
                    json!({
                        "type": "function",
                        "name": tool.name,
                        "description": tool.description,
                        "parameters": tool.parameters,
                    })
                })
                .collect(),
        );
    }
    body
}

fn uuid_like() -> String {
    format!(
        "{:x}{:x}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or(0)
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn adapter() -> OpenAiResponsesAdapter {
        OpenAiResponsesAdapter::new("https://example.invalid/custom/responses", None)
    }

    #[test]
    fn parses_text_stream() {
        let stream = concat!(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}\n\n",
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}\n\n",
        );
        let mut text = String::new();
        let outcome = adapter()
            .parse_stream(Cursor::new(stream), &AtomicBool::new(false), &mut |event| {
                if let ChatEvent::TextDelta(chunk) = event {
                    text.push_str(&chunk);
                }
            })
            .unwrap();
        assert_eq!(text, "Hello");
        assert_eq!(outcome.text, "Hello");
        assert_eq!(outcome.usage.input_tokens, 3);
        assert_eq!(outcome.usage.output_tokens, 2);
        assert_eq!(outcome.finish_reason, FinishReason::Stop);
    }

    #[test]
    fn parses_tool_call_stream() {
        let stream = concat!(
            "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"read_file\",\"arguments\":\"\"}}\n\n",
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"{\\\"path\\\":\"}\n\n",
            "data: {\"type\":\"response.function_call_arguments.delta\",\"output_index\":0,\"delta\":\"\\\"a.txt\\\"}\"}\n\n",
            "data: {\"type\":\"response.function_call_arguments.done\",\"output_index\":0,\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}\n\n",
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":2}}}\n\n",
        );
        let outcome = adapter()
            .parse_stream(Cursor::new(stream), &AtomicBool::new(false), &mut |_| {})
            .unwrap();
        assert_eq!(outcome.finish_reason, FinishReason::ToolCalls);
        assert_eq!(outcome.tool_calls.len(), 1);
        assert_eq!(outcome.tool_calls[0].id, "call_1");
        assert_eq!(outcome.tool_calls[0].name, "read_file");
        assert_eq!(outcome.tool_calls[0].arguments, "{\"path\":\"a.txt\"}");
    }
}
