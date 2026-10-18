use crate::{
    CancellationToken, ChatEvent, ChatMessage, ChatOutcome, ChatRequest, FinishReason, Protocol,
    ProtocolAdapter, ProviderError, ProviderErrorKind, Role, ToolCall, ToolDefinition, Usage,
    http_post_stream, read_sse,
};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader};

pub struct OpenAiChatAdapter {
    endpoint: String,
    api_key: Option<String>,
}

impl OpenAiChatAdapter {
    /// `endpoint` is the complete native Chat Completions request URL.
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
        let mut finish: Option<String> = None;
        let mut tool_calls: BTreeMap<usize, PartialToolCall> = BTreeMap::new();

        read_sse(reader, cancel, &mut |data| {
            if data.trim() == "[DONE]" {
                return Ok(());
            }
            let value: Value = serde_json::from_str(data)
                .map_err(|error| ProviderError::invalid(format!("bad OpenAI SSE JSON: {error}")))?;

            if let Some(error) = value.get("error") {
                let message = error
                    .get("message")
                    .and_then(Value::as_str)
                    .unwrap_or("OpenAI stream error");
                return Err(ProviderError::new(ProviderErrorKind::Server, message));
            }

            if let Some(usage_value) = value.get("usage").filter(|usage| usage.is_object()) {
                usage = Usage {
                    input_tokens: usage_value
                        .get("prompt_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or(usage.input_tokens),
                    output_tokens: usage_value
                        .get("completion_tokens")
                        .and_then(Value::as_u64)
                        .unwrap_or(usage.output_tokens),
                };
                on_event(ChatEvent::Usage(usage));
            }

            let Some(choices) = value.get("choices").and_then(Value::as_array) else {
                return Ok(());
            };
            for choice in choices {
                if let Some(delta) = choice.get("delta") {
                    if let Some(content) = delta.get("content").and_then(Value::as_str) {
                        if !content.is_empty() {
                            text.push_str(content);
                            on_event(ChatEvent::TextDelta(content.to_owned()));
                        }
                    }
                    if let Some(reasoning) = delta
                        .get("reasoning_content")
                        .or_else(|| delta.get("reasoning"))
                        .and_then(Value::as_str)
                    {
                        if !reasoning.is_empty() {
                            on_event(ChatEvent::ReasoningDelta(reasoning.to_owned()));
                        }
                    }
                    if let Some(calls) = delta.get("tool_calls").and_then(Value::as_array) {
                        for call in calls {
                            let index =
                                call.get("index").and_then(Value::as_u64).unwrap_or(0) as usize;
                            let id = call.get("id").and_then(Value::as_str);
                            let name = call
                                .get("function")
                                .and_then(|function| function.get("name"))
                                .and_then(Value::as_str);
                            let arguments = call
                                .get("function")
                                .and_then(|function| function.get("arguments"))
                                .and_then(Value::as_str);
                            let partial = tool_calls.entry(index).or_default();
                            if let Some(id) = id {
                                partial.id = id.to_owned();
                            }
                            if let Some(name) = name {
                                partial.name = name.to_owned();
                            }
                            if let Some(arguments) = arguments {
                                partial.arguments.push_str(arguments);
                            }
                            on_event(ChatEvent::ToolCallDelta {
                                index,
                                id: id.map(str::to_owned),
                                name: name.map(str::to_owned),
                                arguments: arguments.map(str::to_owned),
                            });
                        }
                    }
                }
                if let Some(reason) = choice.get("finish_reason").and_then(Value::as_str) {
                    finish = Some(reason.to_owned());
                }
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
                arguments: call.arguments,
            })
            .collect::<Vec<_>>();

        let finish_reason = if !tool_calls.is_empty() {
            FinishReason::ToolCalls
        } else {
            match finish.as_deref() {
                Some("tool_calls") => FinishReason::ToolCalls,
                Some("length") => FinishReason::Length,
                Some("stop") | None => FinishReason::Stop,
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

impl ProtocolAdapter for OpenAiChatAdapter {
    fn protocol(&self) -> Protocol {
        Protocol::OpenAiChat
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
            return Err(ProviderError::config(
                "OpenAI Chat API key is not configured",
            ));
        };
        let body = openai_body(request);
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

pub fn openai_body(request: &ChatRequest) -> Value {
    let mut messages = Vec::new();
    if !request.system.is_empty() {
        messages.push(json!({"role": "system", "content": request.system}));
    }
    messages.extend(request.messages.iter().map(openai_message));

    let mut body = json!({
        "model": request.model,
        "messages": messages,
        "stream": true,
        "stream_options": {"include_usage": true},
        "max_tokens": request.max_tokens,
        "temperature": request.temperature,
    });

    if !request.tools.is_empty() {
        body["tools"] = Value::Array(request.tools.iter().map(openai_tool).collect());
    }
    body
}

fn openai_message(message: &ChatMessage) -> Value {
    match message.role {
        Role::System => json!({"role": "system", "content": message.text()}),
        Role::User => json!({"role": "user", "content": message.text()}),
        Role::Tool => json!({
            "role": "tool",
            "tool_call_id": message.tool_call_id,
            "content": message.text(),
        }),
        Role::Assistant => {
            let mut value = json!({"role": "assistant", "content": message.text()});
            if message.text().is_empty() {
                value["content"] = Value::Null;
            }
            if !message.tool_calls.is_empty() {
                value["tool_calls"] = Value::Array(
                    message
                        .tool_calls
                        .iter()
                        .map(|call| {
                            json!({
                                "id": call.id,
                                "type": "function",
                                "function": {
                                    "name": call.name,
                                    "arguments": call.arguments,
                                }
                            })
                        })
                        .collect(),
                );
            }
            value
        }
    }
}

fn openai_tool(tool: &ToolDefinition) -> Value {
    json!({
        "type": "function",
        "function": {
            "name": tool.name,
            "description": tool.description,
            "parameters": tool.parameters,
        }
    })
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

    fn adapter() -> OpenAiChatAdapter {
        OpenAiChatAdapter::new("https://example.invalid/custom/chat", None)
    }

    #[test]
    fn parses_text_and_usage() {
        let stream = concat!(
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"},\"finish_reason\":\"stop\"}],",
            "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\n",
            "data: [DONE]\n\n",
        );
        let mut deltas = String::new();
        let outcome = adapter()
            .parse_stream(
                Cursor::new(stream),
                &CancellationToken::new(),
                &mut |event| {
                    if let ChatEvent::TextDelta(text) = event {
                        deltas.push_str(&text);
                    }
                },
            )
            .unwrap();
        assert_eq!(deltas, "Hello");
        assert_eq!(outcome.text, "Hello");
        assert_eq!(outcome.usage.input_tokens, 3);
        assert_eq!(outcome.usage.output_tokens, 2);
        assert_eq!(outcome.finish_reason, FinishReason::Stop);
    }

    #[test]
    fn parses_streamed_tool_call() {
        let stream = concat!(
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",",
            "\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\"}}]}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,",
            "\"function\":{\"arguments\":\"\\\"a.txt\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n",
            "data: [DONE]\n\n",
        );
        let outcome = adapter()
            .parse_stream(Cursor::new(stream), &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(outcome.finish_reason, FinishReason::ToolCalls);
        assert_eq!(outcome.tool_calls.len(), 1);
        assert_eq!(outcome.tool_calls[0].id, "call_1");
        assert_eq!(outcome.tool_calls[0].name, "read_file");
        assert_eq!(outcome.tool_calls[0].arguments, "{\"path\":\"a.txt\"}");
    }
}
