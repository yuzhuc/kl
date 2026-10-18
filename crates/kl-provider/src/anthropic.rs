use crate::{
    CancellationToken, ChatEvent, ChatOutcome, ChatRequest, ContentPart, FinishReason, Protocol,
    ProtocolAdapter, ProviderError, ProviderErrorKind, Role, ToolCall, Usage, http_post_stream,
    read_sse,
};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader};

pub struct AnthropicAdapter {
    endpoint: String,
    api_key: Option<String>,
}

impl AnthropicAdapter {
    /// `endpoint` is the complete native Messages request URL.
    pub fn new(endpoint: impl Into<String>, api_key: Option<String>) -> Self {
        Self {
            endpoint: endpoint.into(),
            api_key,
        }
    }

    pub fn parse_stream<R: BufRead>(
        &self,
        reader: R,
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let mut text = String::new();
        let mut usage = Usage::default();
        let mut stop_reason: Option<String> = None;
        let mut tool_calls: BTreeMap<usize, PartialToolCall> = BTreeMap::new();

        read_sse(reader, cancel, &mut |data| {
            let value: Value = serde_json::from_str(data).map_err(|error| {
                ProviderError::invalid(format!("bad Anthropic SSE JSON: {error}"))
            })?;
            let event_type = value
                .get("type")
                .and_then(Value::as_str)
                .unwrap_or_default();
            match event_type {
                "message_start" => {
                    if let Some(input) = value
                        .get("message")
                        .and_then(|message| message.get("usage"))
                        .and_then(|usage| usage.get("input_tokens"))
                        .and_then(Value::as_u64)
                    {
                        usage.input_tokens = input;
                    }
                }
                "content_block_start" => {
                    let index = value.get("index").and_then(Value::as_u64).unwrap_or(0) as usize;
                    let Some(block) = value.get("content_block") else {
                        return Ok(());
                    };
                    match block.get("type").and_then(Value::as_str) {
                        Some("text") => {
                            if let Some(initial) = block.get("text").and_then(Value::as_str) {
                                if !initial.is_empty() {
                                    text.push_str(initial);
                                    on_event(ChatEvent::TextDelta(initial.to_owned()));
                                }
                            }
                        }
                        Some("tool_use") => {
                            let partial = tool_calls.entry(index).or_default();
                            partial.id = block
                                .get("id")
                                .and_then(Value::as_str)
                                .unwrap_or_default()
                                .to_owned();
                            partial.name = block
                                .get("name")
                                .and_then(Value::as_str)
                                .unwrap_or_default()
                                .to_owned();
                            on_event(ChatEvent::ToolCallDelta {
                                index,
                                id: Some(partial.id.clone()),
                                name: Some(partial.name.clone()),
                                arguments: None,
                            });
                        }
                        _ => {}
                    }
                }
                "content_block_delta" => {
                    let index = value.get("index").and_then(Value::as_u64).unwrap_or(0) as usize;
                    let Some(delta) = value.get("delta") else {
                        return Ok(());
                    };
                    match delta.get("type").and_then(Value::as_str) {
                        Some("text_delta") => {
                            if let Some(chunk) = delta.get("text").and_then(Value::as_str) {
                                if !chunk.is_empty() {
                                    text.push_str(chunk);
                                    on_event(ChatEvent::TextDelta(chunk.to_owned()));
                                }
                            }
                        }
                        Some("thinking_delta") => {
                            if let Some(chunk) = delta.get("thinking").and_then(Value::as_str) {
                                if !chunk.is_empty() {
                                    on_event(ChatEvent::ReasoningDelta(chunk.to_owned()));
                                }
                            }
                        }
                        Some("input_json_delta") => {
                            let partial = delta
                                .get("partial_json")
                                .and_then(Value::as_str)
                                .unwrap_or_default();
                            if !partial.is_empty() {
                                tool_calls
                                    .entry(index)
                                    .or_default()
                                    .arguments
                                    .push_str(partial);
                                on_event(ChatEvent::ToolCallDelta {
                                    index,
                                    id: None,
                                    name: None,
                                    arguments: Some(partial.to_owned()),
                                });
                            }
                        }
                        _ => {}
                    }
                }
                "message_delta" => {
                    if let Some(output) = value
                        .get("usage")
                        .and_then(|usage| usage.get("output_tokens"))
                        .and_then(Value::as_u64)
                    {
                        usage.output_tokens = output;
                        on_event(ChatEvent::Usage(usage));
                    }
                    if let Some(reason) = value
                        .get("delta")
                        .and_then(|delta| delta.get("stop_reason"))
                        .and_then(Value::as_str)
                    {
                        stop_reason = Some(reason.to_owned());
                    }
                }
                "error" => {
                    let message = value
                        .get("error")
                        .and_then(|error| error.get("message"))
                        .and_then(Value::as_str)
                        .unwrap_or("Anthropic stream error");
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
                id: call.id,
                name: call.name,
                arguments: if call.arguments.is_empty() {
                    "{}".to_owned()
                } else {
                    call.arguments
                },
            })
            .collect::<Vec<_>>();

        let finish_reason = if !tool_calls.is_empty() {
            FinishReason::ToolCalls
        } else {
            match stop_reason.as_deref() {
                Some("tool_use") => FinishReason::ToolCalls,
                Some("max_tokens") => FinishReason::Length,
                Some("end_turn") | Some("stop_sequence") | None => FinishReason::Stop,
                Some(other) => FinishReason::Other(other.to_owned()),
            }
        };

        Ok(ChatOutcome {
            text,
            tool_calls,
            finish_reason,
            usage,
        })
    }
}

impl ProtocolAdapter for AnthropicAdapter {
    fn protocol(&self) -> Protocol {
        Protocol::Anthropic
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
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let Some(api_key) = &self.api_key else {
            return Err(ProviderError::config("Anthropic API key is not configured"));
        };
        let body = anthropic_body(request);
        let headers = [
            ("x-api-key", api_key.clone()),
            ("anthropic-version", "2023-06-01".to_owned()),
        ];
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

pub fn anthropic_body(request: &ChatRequest) -> Value {
    let mut body = json!({
        "model": request.model,
        "max_tokens": request.max_tokens,
        "messages": anthropic_messages(request),
        "stream": true,
        "temperature": request.temperature,
    });
    if !request.system.is_empty() {
        body["system"] = json!(request.system);
    }
    if !request.tools.is_empty() {
        body["tools"] = Value::Array(
            request
                .tools
                .iter()
                .map(|tool| {
                    json!({
                        "name": tool.name,
                        "description": tool.description,
                        "input_schema": tool.parameters,
                    })
                })
                .collect(),
        );
    }
    body
}

fn anthropic_messages(request: &ChatRequest) -> Vec<Value> {
    let mut messages: Vec<Value> = Vec::new();
    for message in &request.messages {
        let (role, blocks) = match message.role {
            Role::System => continue,
            Role::User => (
                "user",
                vec![json!({"type": "text", "text": message.text()})],
            ),
            Role::Tool => (
                "user",
                vec![json!({
                    "type": "tool_result",
                    "tool_use_id": message.tool_call_id,
                    "content": message.text(),
                })],
            ),
            Role::Assistant => {
                let mut blocks = Vec::new();
                let text = message
                    .content
                    .iter()
                    .map(|part| match part {
                        ContentPart::Text(text) => text.as_str(),
                    })
                    .collect::<Vec<_>>()
                    .join("");
                if !text.is_empty() {
                    blocks.push(json!({"type": "text", "text": text}));
                }
                for call in &message.tool_calls {
                    let input: Value =
                        serde_json::from_str(&call.arguments).unwrap_or_else(|_| json!({}));
                    blocks.push(json!({
                        "type": "tool_use",
                        "id": call.id,
                        "name": call.name,
                        "input": input,
                    }));
                }
                ("assistant", blocks)
            }
        };

        if let Some(last) = messages.last_mut() {
            if last.get("role").and_then(Value::as_str) == Some(role) {
                if let Some(content) = last.get_mut("content").and_then(Value::as_array_mut) {
                    content.extend(blocks);
                    continue;
                }
            }
        }
        messages.push(json!({"role": role, "content": blocks}));
    }
    messages
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    fn adapter() -> AnthropicAdapter {
        AnthropicAdapter::new("https://example.invalid/custom/messages", None)
    }

    #[test]
    fn parses_text_stream() {
        let stream = concat!(
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n",
            "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n",
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\n",
            "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n",
            "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
        );
        let mut text = String::new();
        let outcome = adapter()
            .parse_stream(
                Cursor::new(stream),
                &CancellationToken::new(),
                &mut |event| {
                    if let ChatEvent::TextDelta(chunk) = event {
                        text.push_str(&chunk);
                    }
                },
            )
            .unwrap();
        assert_eq!(text, "Hi");
        assert_eq!(outcome.usage.input_tokens, 5);
        assert_eq!(outcome.usage.output_tokens, 2);
        assert_eq!(outcome.finish_reason, FinishReason::Stop);
    }

    #[test]
    fn parses_tool_use_stream() {
        let stream = concat!(
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"read_file\",\"input\":{}}}\n\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}\n\n",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":9}}\n\n",
        );
        let outcome = adapter()
            .parse_stream(Cursor::new(stream), &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(outcome.finish_reason, FinishReason::ToolCalls);
        assert_eq!(outcome.tool_calls.len(), 1);
        assert_eq!(outcome.tool_calls[0].name, "read_file");
        assert_eq!(outcome.tool_calls[0].arguments, "{\"path\":\"a.txt\"}");
    }
}
