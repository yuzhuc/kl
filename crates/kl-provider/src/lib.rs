//! Provider-neutral chat request/response types, provider routing and the
//! streaming adapters. The agent never branches on provider names.

mod anthropic;
mod openai;
mod scripted;

pub use anthropic::AnthropicProvider;
pub use openai::OpenAiChatProvider;
pub use scripted::ScriptedProvider;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::io::{BufRead, Read};
use std::sync::Arc;
use std::time::Duration;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Role {
    System,
    User,
    Assistant,
    Tool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum ContentPart {
    #[serde(rename = "text")]
    Text(String),
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ToolCall {
    pub id: String,
    pub name: String,
    /// Raw JSON string emitted by the model.
    pub arguments: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: Role,
    #[serde(default)]
    pub content: Vec<ContentPart>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool_call_id: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub tool_calls: Vec<ToolCall>,
}

impl ChatMessage {
    pub fn system(text: impl Into<String>) -> Self {
        Self {
            role: Role::System,
            content: vec![ContentPart::Text(text.into())],
            tool_call_id: None,
            tool_calls: Vec::new(),
        }
    }

    pub fn user(text: impl Into<String>) -> Self {
        Self {
            role: Role::User,
            content: vec![ContentPart::Text(text.into())],
            tool_call_id: None,
            tool_calls: Vec::new(),
        }
    }

    pub fn assistant(text: impl Into<String>, tool_calls: Vec<ToolCall>) -> Self {
        Self::assistant_with_text(text, tool_calls)
    }

    pub fn assistant_with_text(text: impl Into<String>, tool_calls: Vec<ToolCall>) -> Self {
        let text = text.into();
        Self {
            role: Role::Assistant,
            content: if text.is_empty() {
                Vec::new()
            } else {
                vec![ContentPart::Text(text)]
            },
            tool_call_id: None,
            tool_calls,
        }
    }

    pub fn tool(call_id: impl Into<String>, text: impl Into<String>) -> Self {
        Self {
            role: Role::Tool,
            content: vec![ContentPart::Text(text.into())],
            tool_call_id: Some(call_id.into()),
            tool_calls: Vec::new(),
        }
    }

    pub fn with_text(mut self, text: impl Into<String>) -> Self {
        let text = text.into();
        if !text.is_empty() {
            self.content = vec![ContentPart::Text(text)];
        }
        self
    }

    pub fn text(&self) -> String {
        self.content
            .iter()
            .map(|part| match part {
                ContentPart::Text(text) => text.as_str(),
            })
            .collect::<Vec<_>>()
            .join("")
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ToolDefinition {
    pub name: String,
    pub description: String,
    pub parameters: Value,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Usage {
    #[serde(default)]
    pub input_tokens: u64,
    #[serde(default)]
    pub output_tokens: u64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ChatRequest {
    pub model: String,
    pub system: String,
    pub messages: Vec<ChatMessage>,
    pub tools: Vec<ToolDefinition>,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl ChatRequest {
    pub fn new(model: impl Into<String>, messages: Vec<ChatMessage>) -> Self {
        Self {
            model: model.into(),
            system: String::new(),
            messages,
            tools: Vec::new(),
            max_tokens: 4096,
            temperature: 0.2,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FinishReason {
    Stop,
    ToolCalls,
    Length,
    Other(String),
}

#[derive(Debug, Clone, PartialEq)]
pub enum ChatEvent {
    TextDelta(String),
    ReasoningDelta(String),
    ToolCallDelta {
        index: usize,
        id: Option<String>,
        name: Option<String>,
        arguments: Option<String>,
    },
    Usage(Usage),
}

#[derive(Debug, Clone, PartialEq)]
pub struct ChatOutcome {
    pub text: String,
    pub tool_calls: Vec<ToolCall>,
    pub finish_reason: FinishReason,
    pub usage: Usage,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelInfo {
    pub id: String,
    pub context_window: Option<u32>,
    pub max_output: Option<u32>,
}

impl ModelInfo {
    pub fn new(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            context_window: None,
            max_output: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProviderErrorKind {
    Config,
    Auth,
    RateLimit,
    Timeout,
    Transport,
    InvalidResponse,
    Server,
}

#[derive(Debug, Clone, thiserror::Error)]
#[error("{kind:?}: {message}")]
pub struct ProviderError {
    pub kind: ProviderErrorKind,
    pub message: String,
    pub status: Option<u16>,
    pub retryable: bool,
}

impl ProviderError {
    pub fn new(kind: ProviderErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
            status: None,
            retryable: matches!(
                kind,
                ProviderErrorKind::RateLimit
                    | ProviderErrorKind::Timeout
                    | ProviderErrorKind::Transport
                    | ProviderErrorKind::Server
            ),
        }
    }

    pub fn config(message: impl Into<String>) -> Self {
        Self::new(ProviderErrorKind::Config, message)
    }

    pub fn invalid(message: impl Into<String>) -> Self {
        Self::new(ProviderErrorKind::InvalidResponse, message)
    }
}

pub trait Provider: Send + Sync {
    fn id(&self) -> &str;
    fn kind(&self) -> &str;
    fn models(&self) -> Vec<ModelInfo>;
    fn available(&self) -> (bool, String) {
        (true, "configured".to_owned())
    }
    fn begin_turn(&self) {}
    fn stream_chat(
        &self,
        request: &ChatRequest,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderInfo {
    pub id: String,
    pub kind: String,
    pub models: Vec<String>,
    pub available: bool,
    pub detail: String,
}

#[derive(Clone)]
pub struct ProviderRouter {
    providers: Vec<Arc<dyn Provider>>,
    default_id: String,
    default_model: String,
}

impl ProviderRouter {
    pub fn new(
        providers: Vec<Arc<dyn Provider>>,
        default_id: impl Into<String>,
        default_model: impl Into<String>,
    ) -> Self {
        Self {
            providers,
            default_id: default_id.into(),
            default_model: default_model.into(),
        }
    }

    pub fn default_id(&self) -> &str {
        &self.default_id
    }

    pub fn default_model(&self) -> &str {
        &self.default_model
    }

    pub fn get(&self, id: Option<&str>) -> Result<Arc<dyn Provider>, ProviderError> {
        let requested = id.unwrap_or(&self.default_id);
        self.providers
            .iter()
            .find(|provider| provider.id() == requested)
            .cloned()
            .ok_or_else(|| ProviderError::config(format!("unknown provider: {requested}")))
    }

    pub fn resolve_model(&self, provider: &dyn Provider, requested: Option<&str>) -> String {
        if let Some(model) = requested.filter(|model| !model.is_empty()) {
            return model.to_owned();
        }
        if provider.id() == self.default_id && !self.default_model.is_empty() {
            return self.default_model.clone();
        }
        provider
            .models()
            .first()
            .map(|model| model.id.clone())
            .unwrap_or_else(|| self.default_model.clone())
    }

    pub fn list(&self) -> Vec<ProviderInfo> {
        self.providers
            .iter()
            .map(|provider| {
                let (available, detail) = provider.available();
                ProviderInfo {
                    id: provider.id().to_owned(),
                    kind: provider.kind().to_owned(),
                    models: provider
                        .models()
                        .into_iter()
                        .map(|model| model.id)
                        .collect(),
                    available,
                    detail,
                }
            })
            .collect()
    }
}

/// Parse a Server-Sent Events stream and pass each `data:` payload to the
/// callback. Multi-line data fields are joined with `\n` per the SSE spec.
pub fn read_sse<R: BufRead>(
    mut reader: R,
    on_data: &mut dyn FnMut(&str) -> Result<(), ProviderError>,
) -> Result<(), ProviderError> {
    let mut line = String::new();
    let mut data = String::new();
    loop {
        line.clear();
        let read = reader
            .read_line(&mut line)
            .map_err(|error| ProviderError::new(ProviderErrorKind::Transport, error.to_string()))?;
        if read == 0 {
            break;
        }
        let trimmed = line.trim_end_matches(['\r', '\n']);
        if trimmed.is_empty() {
            if !data.is_empty() {
                on_data(&data)?;
                data.clear();
            }
            continue;
        }
        if trimmed.starts_with(':') {
            continue;
        }
        if let Some(rest) = trimmed.strip_prefix("data:") {
            let value = rest.strip_prefix(' ').unwrap_or(rest);
            if !data.is_empty() {
                data.push('\n');
            }
            data.push_str(value);
        }
    }
    if !data.is_empty() {
        on_data(&data)?;
    }
    Ok(())
}

pub(crate) fn http_post_stream(
    url: &str,
    headers: &[(&str, String)],
    body: &Value,
) -> Result<Box<dyn Read + Send + Sync>, ProviderError> {
    let agent = ureq::AgentBuilder::new()
        .timeout_connect(Duration::from_secs(20))
        .timeout_read(Duration::from_secs(300))
        .timeout_write(Duration::from_secs(60))
        .build();
    let mut request = agent
        .post(url)
        .set("content-type", "application/json")
        .set("accept", "text/event-stream");
    for (name, value) in headers {
        request = request.set(name, value);
    }
    let payload = serde_json::to_string(body).map_err(|error| {
        ProviderError::invalid(format!("cannot encode provider request: {error}"))
    })?;
    match request.send_string(&payload) {
        Ok(response) => Ok(response.into_reader()),
        Err(ureq::Error::Status(status, response)) => {
            let text = response.into_string().unwrap_or_default();
            Err(normalize_http_error(status, &text))
        }
        Err(ureq::Error::Transport(error)) => {
            let message = error.to_string();
            let kind = if message.to_lowercase().contains("timed out")
                || message.to_lowercase().contains("timeout")
            {
                ProviderErrorKind::Timeout
            } else {
                ProviderErrorKind::Transport
            };
            Err(ProviderError::new(kind, message))
        }
    }
}

fn normalize_http_error(status: u16, body: &str) -> ProviderError {
    let kind = match status {
        401 | 403 => ProviderErrorKind::Auth,
        429 => ProviderErrorKind::RateLimit,
        500..=599 => ProviderErrorKind::Server,
        _ => ProviderErrorKind::InvalidResponse,
    };
    let mut error = ProviderError::new(kind, format!("HTTP {status}: {body}"));
    error.status = Some(status);
    error
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Cursor;

    #[test]
    fn sse_joins_multiline_data() {
        let stream = "event: message\n: comment\ndata: {\"a\":\ndata: 1}\n\n";
        let mut payloads = Vec::new();
        read_sse(Cursor::new(stream), &mut |data| {
            payloads.push(data.to_owned());
            Ok(())
        })
        .unwrap();
        assert_eq!(payloads, vec!["{\"a\":\n1}"]);
    }

    #[test]
    fn chat_message_text_concatenates_parts() {
        let message = ChatMessage::assistant_with_text("hello", Vec::new());
        assert_eq!(message.text(), "hello");
    }
}
