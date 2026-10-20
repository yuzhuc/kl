use crate::{
    CancellationToken, ChatEvent, ChatOutcome, ChatRequest, FinishReason, ModelInfo, Provider,
    ProviderError, ProviderErrorKind, ToolCall, Usage,
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};

/// Deterministic provider used for offline end-to-end tests and demos.
/// It returns pre-recorded assistant turns in order, resetting for each turn.
pub struct ScriptedProvider {
    id: String,
    models: Vec<ModelInfo>,
    turns: Vec<ScriptTurn>,
    cursor: AtomicUsize,
    calls: AtomicUsize,
    reset_on_begin_turn: bool,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ScriptTurn {
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub reasoning: String,
    #[serde(default)]
    pub tool_calls: Vec<ScriptToolCall>,
    /// Scripted failure: the turn fails instead of streaming an answer.
    #[serde(default)]
    pub error: Option<ScriptError>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ScriptError {
    pub kind: ProviderErrorKind,
    #[serde(default)]
    pub message: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ScriptToolCall {
    #[serde(default)]
    pub id: Option<String>,
    pub name: String,
    #[serde(default)]
    pub arguments: Value,
}

impl ScriptedProvider {
    pub fn new(id: impl Into<String>, turns: Vec<ScriptTurn>) -> Self {
        Self {
            id: id.into(),
            models: vec![ModelInfo::new("scripted")],
            turns,
            cursor: AtomicUsize::new(0),
            calls: AtomicUsize::new(0),
            reset_on_begin_turn: true,
        }
    }

    /// Keep advancing through the script across evaluation turns.
    pub fn sequential(id: impl Into<String>, turns: Vec<ScriptTurn>) -> Self {
        let mut provider = Self::new(id, turns);
        provider.reset_on_begin_turn = false;
        provider
    }

    pub fn from_json(id: impl Into<String>, json: &str) -> Result<Self, ProviderError> {
        let turns: Vec<ScriptTurn> = serde_json::from_str(json)
            .map_err(|error| ProviderError::config(format!("invalid script: {error}")))?;
        Ok(Self::new(id, turns))
    }

    pub fn from_file(id: impl Into<String>, path: impl AsRef<Path>) -> Result<Self, ProviderError> {
        Self::from_file_with_reset(id, path, true)
    }

    pub fn from_file_sequential(
        id: impl Into<String>,
        path: impl AsRef<Path>,
    ) -> Result<Self, ProviderError> {
        Self::from_file_with_reset(id, path, false)
    }

    fn from_file_with_reset(
        id: impl Into<String>,
        path: impl AsRef<Path>,
        reset_on_begin_turn: bool,
    ) -> Result<Self, ProviderError> {
        let path = path.as_ref();
        let text = std::fs::read_to_string(path).map_err(|error| {
            ProviderError::config(format!("cannot read script {}: {error}", path.display()))
        })?;
        let turns: Vec<ScriptTurn> = serde_json::from_str(&text)
            .map_err(|error| ProviderError::config(format!("invalid script: {error}")))?;
        let mut provider = Self::new(id, turns);
        provider.reset_on_begin_turn = reset_on_begin_turn;
        Ok(provider)
    }

    pub fn empty(id: impl Into<String>) -> Self {
        Self::new(id, Vec::new())
    }

    /// Number of `stream_chat` calls served so far.
    pub fn call_count(&self) -> usize {
        self.calls.load(Ordering::SeqCst)
    }
}

impl Provider for ScriptedProvider {
    fn id(&self) -> &str {
        &self.id
    }

    fn kind(&self) -> &str {
        "scripted"
    }

    fn models(&self) -> Vec<ModelInfo> {
        self.models.clone()
    }

    fn available(&self) -> (bool, String) {
        if self.turns.is_empty() {
            (false, "no script configured".to_owned())
        } else {
            (true, format!("{} scripted turns", self.turns.len()))
        }
    }

    fn begin_turn(&self) {
        if self.reset_on_begin_turn {
            self.cursor.store(0, Ordering::SeqCst);
        }
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        if cancel.is_cancelled() {
            return Err(ProviderError::cancelled());
        }
        let index = self.cursor.fetch_add(1, Ordering::SeqCst);
        let Some(turn) = self.turns.get(index) else {
            on_event(ChatEvent::TextDelta("Script exhausted.".to_owned()));
            return Ok(ChatOutcome {
                text: "Script exhausted.".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            });
        };

        if !turn.reasoning.is_empty() {
            on_event(ChatEvent::ReasoningDelta(turn.reasoning.clone()));
        }
        for chunk in chunk_text(&turn.text, 24) {
            if cancel.is_cancelled() {
                return Err(ProviderError::cancelled());
            }
            on_event(ChatEvent::TextDelta(chunk));
        }
        if let Some(error) = &turn.error {
            return Err(ProviderError::new(error.kind, error.message.clone()));
        }

        let mut calls = Vec::new();
        for (call_index, call) in turn.tool_calls.iter().enumerate() {
            let id = call
                .id
                .clone()
                .unwrap_or_else(|| format!("call-{index}-{call_index}"));
            let arguments = if call.arguments.is_null() {
                json!({})
            } else {
                call.arguments.clone()
            };
            let arguments = serde_json::to_string(&arguments).unwrap_or_else(|_| "{}".to_owned());
            on_event(ChatEvent::ToolCallDelta {
                index: call_index,
                id: Some(id.clone()),
                name: Some(call.name.clone()),
                arguments: Some(arguments.clone()),
            });
            calls.push(ToolCall {
                id,
                name: call.name.clone(),
                arguments,
            });
        }

        Ok(ChatOutcome {
            text: turn.text.clone(),
            tool_calls: calls.clone(),
            finish_reason: if calls.is_empty() {
                FinishReason::Stop
            } else {
                FinishReason::ToolCalls
            },
            usage: Usage::default(),
        })
    }
}

fn chunk_text(text: &str, size: usize) -> Vec<String> {
    if text.is_empty() {
        return Vec::new();
    }
    let characters: Vec<char> = text.chars().collect();
    characters
        .chunks(size)
        .map(|chunk| chunk.iter().collect())
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn returns_scripted_turns_and_resets() {
        let provider = ScriptedProvider::from_json(
            "scripted",
            r#"[{"tool_calls":[{"name":"read_file","arguments":{"path":"a.txt"}}]},{"text":"done"}]"#,
        )
        .unwrap();
        let request = ChatRequest::new("scripted", vec![]);
        let first = provider
            .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(first.tool_calls.len(), 1);
        assert_eq!(first.tool_calls[0].name, "read_file");

        let second = provider
            .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(second.text, "done");
        assert_eq!(second.finish_reason, FinishReason::Stop);

        provider.begin_turn();
        let reset = provider
            .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(reset.tool_calls.len(), 1);
    }

    #[test]
    fn sequential_mode_keeps_the_cursor_across_turns() {
        let provider = ScriptedProvider::sequential(
            "scripted",
            vec![
                ScriptTurn {
                    text: "one".to_owned(),
                    reasoning: String::new(),
                    tool_calls: Vec::new(),
                    error: None,
                },
                ScriptTurn {
                    text: "two".to_owned(),
                    reasoning: String::new(),
                    tool_calls: Vec::new(),
                    error: None,
                },
            ],
        );
        let request = ChatRequest::new("scripted", vec![]);
        let first = provider
            .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
            .unwrap();
        provider.begin_turn();
        let second = provider
            .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
            .unwrap();
        assert_eq!(first.text, "one");
        assert_eq!(second.text, "two");
    }
}
