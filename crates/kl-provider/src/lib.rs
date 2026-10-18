//! Provider-neutral chat request/response types, per-model routing and the
//! three native protocol adapters. The agent never branches on provider names
//! or wire protocols.

mod anthropic;
mod openai;
mod responses;
mod scripted;

pub use anthropic::AnthropicAdapter;
pub use openai::OpenAiChatAdapter;
pub use responses::OpenAiResponsesAdapter;
pub use scripted::ScriptedProvider;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashSet;
use std::io::{BufRead, Read};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, RwLock};
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Default)]
pub struct CancellationToken {
    flag: Arc<AtomicBool>,
}

impl CancellationToken {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_arc(flag: Arc<AtomicBool>) -> Self {
        Self { flag }
    }

    pub fn cancel(&self) {
        self.flag.store(true, Ordering::Relaxed);
    }

    pub fn is_cancelled(&self) -> bool {
        self.flag.load(Ordering::Relaxed)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Protocol {
    #[serde(rename = "openai-chat")]
    OpenAiChat,
    #[serde(rename = "anthropic")]
    Anthropic,
    #[serde(rename = "openai-responses")]
    OpenAiResponses,
    #[serde(rename = "scripted")]
    Scripted,
    #[serde(rename = "plugin")]
    Plugin,
}

impl Protocol {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::OpenAiChat => "openai-chat",
            Self::Anthropic => "anthropic",
            Self::OpenAiResponses => "openai-responses",
            Self::Scripted => "scripted",
            Self::Plugin => "plugin",
        }
    }

    pub fn is_native(self) -> bool {
        matches!(
            self,
            Self::OpenAiChat | Self::Anthropic | Self::OpenAiResponses
        )
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ModelMetadata {
    pub provider: String,
    pub model: String,
    pub protocol: Protocol,
    pub url: String,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
}

impl ModelMetadata {
    pub fn new(
        provider: impl Into<String>,
        model: impl Into<String>,
        protocol: Protocol,
        url: impl Into<String>,
        context_window: u32,
        compact_threshold: f32,
        max_output_tokens: u32,
    ) -> Self {
        Self {
            provider: provider.into(),
            model: model.into(),
            protocol,
            url: url.into(),
            context_window,
            compact_threshold,
            max_output_tokens,
        }
    }
}

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

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
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

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FinishReason {
    Stop,
    ToolCalls,
    Length,
    Other(String),
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
pub enum ChatEvent {
    #[serde(rename = "text_delta")]
    TextDelta(String),
    #[serde(rename = "reasoning_delta")]
    ReasoningDelta(String),
    #[serde(rename = "tool_call_delta")]
    ToolCallDelta {
        index: usize,
        id: Option<String>,
        name: Option<String>,
        arguments: Option<String>,
    },
    #[serde(rename = "usage")]
    Usage(Usage),
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatOutcome {
    pub text: String,
    pub tool_calls: Vec<ToolCall>,
    pub finish_reason: FinishReason,
    pub usage: Usage,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
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

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderErrorKind {
    Config,
    Auth,
    RateLimit,
    Timeout,
    Transport,
    InvalidResponse,
    Server,
    Cancelled,
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

    pub fn cancelled() -> Self {
        Self::new(ProviderErrorKind::Cancelled, "provider stream cancelled")
    }
}

pub trait ProtocolAdapter: Send + Sync {
    fn protocol(&self) -> Protocol;
    fn available(&self) -> (bool, String) {
        (true, "configured".to_owned())
    }
    fn stream_chat(
        &self,
        request: &ChatRequest,
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError>;
}

/// A provider bound to one configured model and one native protocol adapter.
pub struct BoundProvider {
    metadata: ModelMetadata,
    adapter: Arc<dyn ProtocolAdapter>,
}

impl BoundProvider {
    pub fn new(metadata: ModelMetadata, adapter: Arc<dyn ProtocolAdapter>) -> Self {
        Self { metadata, adapter }
    }

    pub fn metadata(&self) -> &ModelMetadata {
        &self.metadata
    }
}

impl Provider for BoundProvider {
    fn id(&self) -> &str {
        &self.metadata.provider
    }

    fn kind(&self) -> &str {
        self.metadata.protocol.as_str()
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo {
            id: self.metadata.model.clone(),
            context_window: Some(self.metadata.context_window),
            max_output: Some(self.metadata.max_output_tokens),
        }]
    }

    fn available(&self) -> (bool, String) {
        self.adapter.available()
    }

    fn stream_chat(
        &self,
        request: &ChatRequest,
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        self.adapter.stream_chat(request, cancel, on_event)
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
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError>;
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProviderModelInfo {
    pub provider: String,
    pub model: String,
    pub protocol: Protocol,
    pub url: String,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    pub enabled: bool,
    pub key_configured: bool,
    pub available: bool,
    pub detail: String,
}

struct RouteEntry {
    metadata: ModelMetadata,
    provider: Arc<dyn Provider>,
}

#[derive(Clone)]
pub struct ProviderRouter {
    routes: Arc<RwLock<Vec<RouteEntry>>>,
    disabled: Arc<RwLock<HashSet<String>>>,
    default_provider: String,
    default_model: String,
}

impl ProviderRouter {
    pub fn new(
        routes: Vec<(ModelMetadata, Arc<dyn Provider>)>,
        default_provider: impl Into<String>,
        default_model: impl Into<String>,
    ) -> Self {
        let router = Self {
            routes: Arc::new(RwLock::new(Vec::new())),
            disabled: Arc::new(RwLock::new(HashSet::new())),
            default_provider: default_provider.into(),
            default_model: default_model.into(),
        };
        for (metadata, provider) in routes {
            router.register(metadata, provider);
        }
        router
    }

    pub fn default_provider(&self) -> &str {
        &self.default_provider
    }

    pub fn default_model(&self) -> &str {
        &self.default_model
    }

    pub fn register(&self, metadata: ModelMetadata, provider: Arc<dyn Provider>) {
        let mut routes = self
            .routes
            .write()
            .unwrap_or_else(|error| error.into_inner());
        routes.retain(|route| {
            !(route.metadata.provider == metadata.provider
                && route.metadata.model == metadata.model)
        });
        routes.push(RouteEntry { metadata, provider });
        routes.sort_by(|left, right| {
            left.metadata
                .provider
                .cmp(&right.metadata.provider)
                .then_with(|| left.metadata.model.cmp(&right.metadata.model))
        });
    }

    pub fn remove_provider(&self, provider_id: &str) -> bool {
        let mut routes = self
            .routes
            .write()
            .unwrap_or_else(|error| error.into_inner());
        let before = routes.len();
        routes.retain(|route| route.metadata.provider != provider_id);
        routes.len() != before
    }

    pub fn disable_provider(&self, provider_id: &str) {
        self.remove_provider(provider_id);
        self.disabled
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .insert(provider_id.to_owned());
    }

    pub fn enable_provider(&self, provider_id: &str) {
        self.disabled
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .remove(provider_id);
    }

    pub fn get(
        &self,
        provider: Option<&str>,
        model: Option<&str>,
    ) -> Result<(Arc<dyn Provider>, ModelMetadata), ProviderError> {
        let provider_id = provider
            .filter(|id| !id.is_empty())
            .unwrap_or(&self.default_provider);
        if provider_id.is_empty() {
            return Err(ProviderError::config(
                "no provider selected; set [agent].provider or pass --provider",
            ));
        }
        if self
            .disabled
            .read()
            .unwrap_or_else(|error| error.into_inner())
            .contains(provider_id)
        {
            return Err(ProviderError::config(format!(
                "provider `{provider_id}` is disabled in config.toml"
            )));
        }

        let routes = self
            .routes
            .read()
            .unwrap_or_else(|error| error.into_inner());
        let provider_routes: Vec<&RouteEntry> = routes
            .iter()
            .filter(|route| route.metadata.provider == provider_id)
            .collect();
        if provider_routes.is_empty() {
            return Err(ProviderError::config(format!(
                "unknown provider `{provider_id}`"
            )));
        }

        let requested_model = model
            .filter(|model| !model.is_empty())
            .map(str::to_owned)
            .or_else(|| {
                (provider_id == self.default_provider && !self.default_model.is_empty())
                    .then(|| self.default_model.clone())
            })
            .or_else(|| {
                provider_routes
                    .first()
                    .map(|route| route.metadata.model.clone())
            })
            .unwrap_or_default();

        provider_routes
            .iter()
            .find(|route| route.metadata.model == requested_model)
            .map(|route| (route.provider.clone(), route.metadata.clone()))
            .ok_or_else(|| {
                let available = provider_routes
                    .iter()
                    .map(|route| route.metadata.model.as_str())
                    .collect::<Vec<_>>()
                    .join(", ");
                ProviderError::config(format!(
                    "unknown model `{requested_model}` for provider `{provider_id}`; available models: {available}"
                ))
            })
    }

    pub fn list(&self) -> Vec<ProviderModelInfo> {
        self.routes
            .read()
            .unwrap_or_else(|error| error.into_inner())
            .iter()
            .map(|route| {
                let (available, detail) = route.provider.available();
                ProviderModelInfo {
                    provider: route.metadata.provider.clone(),
                    model: route.metadata.model.clone(),
                    protocol: route.metadata.protocol,
                    url: route.metadata.url.clone(),
                    context_window: route.metadata.context_window,
                    compact_threshold: route.metadata.compact_threshold,
                    max_output_tokens: route.metadata.max_output_tokens,
                    enabled: true,
                    key_configured: available,
                    available,
                    detail,
                }
            })
            .collect()
    }

    pub fn mark_disabled(&self, provider_id: &str) {
        self.disabled
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .insert(provider_id.to_owned());
    }
}

/// Parse a Server-Sent Events stream and pass each `data:` payload to the
/// callback. Multi-line data fields are joined with `\n` per the SSE spec.
pub fn read_sse<R: BufRead>(
    mut reader: R,
    cancel: &CancellationToken,
    on_data: &mut dyn FnMut(&str) -> Result<(), ProviderError>,
) -> Result<(), ProviderError> {
    let started = Instant::now();
    let mut line = String::new();
    let mut data = String::new();
    loop {
        if cancel.is_cancelled() {
            return Err(ProviderError::cancelled());
        }
        if started.elapsed() > Duration::from_secs(300) {
            return Err(ProviderError::new(
                ProviderErrorKind::Timeout,
                "provider stream idle timeout",
            ));
        }
        line.clear();
        let read = match reader.read_line(&mut line) {
            Ok(read) => read,
            Err(error) if is_timeout_error(&error) => {
                if cancel.is_cancelled() {
                    return Err(ProviderError::cancelled());
                }
                continue;
            }
            Err(error) => {
                return Err(ProviderError::new(
                    ProviderErrorKind::Transport,
                    error.to_string(),
                ));
            }
        };
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
    if cancel.is_cancelled() {
        return Err(ProviderError::cancelled());
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
        .timeout_read(Duration::from_millis(500))
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

fn is_timeout_error(error: &std::io::Error) -> bool {
    matches!(
        error.kind(),
        std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
    ) || error.to_string().to_ascii_lowercase().contains("timed out")
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
}
