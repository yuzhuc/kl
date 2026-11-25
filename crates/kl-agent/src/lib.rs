//! Explicit agent loop. Provider differences never appear here: the loop sees
//! one neutral chat request/stream and one neutral tool call type.

use kl_audit::{AuditRecord, AuditSink};
use kl_policy::{Effect, PolicyEngine, ToolAccess};
use kl_protocol::{ApprovalDecision, CoreEvent};
use kl_provider::{
    CancellationToken, ChatEvent, ChatMessage, ChatOutcome, ChatRequest, ContentPart,
    ModelMetadata, Provider, ProviderError, ProviderErrorKind, Role, ToolCall, ToolDefinition,
    Usage,
};
use kl_sandbox::SandboxManager;
use kl_tools::{PolicyRequest, ToolContext, ToolError, ToolRegistry};
use serde_json::{Value, json};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone)]
pub struct AgentConfig {
    pub max_iterations: usize,
    pub max_tool_calls_per_turn: usize,
    pub max_tool_output_bytes: usize,
    pub command_timeout: Duration,
    /// Zero means unlimited. Providers that omit usage are estimated conservatively.
    pub max_total_tokens: u64,
    pub tool_access: ToolAccess,
}

impl Default for AgentConfig {
    fn default() -> Self {
        Self {
            max_iterations: 12,
            max_tool_calls_per_turn: 32,
            max_tool_output_bytes: 32 * 1024,
            command_timeout: Duration::from_secs(120),
            max_total_tokens: 0,
            tool_access: ToolAccess::Full,
        }
    }
}

/// Retry policy for transient provider failures.
#[derive(Debug, Clone, Copy)]
struct RetryPolicy {
    /// Provider attempts per request, including the first one.
    max_attempts: u32,
    /// Nominal wait before the second attempt; every further wait doubles it.
    base_delay: Duration,
}

impl Default for RetryPolicy {
    fn default() -> Self {
        Self {
            max_attempts: 3,
            base_delay: Duration::from_millis(250),
        }
    }
}

impl RetryPolicy {
    /// Nominal cap of the wait before the attempt that follows `retry`
    /// retries (0-based).
    fn cap(&self, retry: u32) -> Duration {
        self.base_delay.saturating_mul(1u32 << retry.min(16))
    }

    /// Wait before the attempt that follows `retry` retries (0-based). Equal
    /// jitter spreads retries out while staying under the nominal delay.
    fn delay(&self, retry: u32) -> Duration {
        let cap = self.cap(retry);
        cap / 2 + jitter(cap / 2)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TurnStatus {
    Completed,
    Cancelled,
}

impl TurnStatus {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Completed => "completed",
            Self::Cancelled => "cancelled",
        }
    }
}

#[derive(Debug, Clone)]
pub struct TurnResult {
    pub status: TurnStatus,
    pub text: String,
    pub usage: Usage,
}

#[derive(Debug, Clone)]
pub struct CompactionResult {
    pub history: Vec<ChatMessage>,
    pub omitted_messages: usize,
    pub estimated_tokens: u64,
    /// True when a model written summary replaced the omitted rounds.
    pub summary_used: bool,
    /// The summary that replaced the omitted rounds, when one was produced.
    pub summary: Option<String>,
}

#[derive(Debug, Clone)]
struct CompactionNotice {
    omitted_messages: usize,
    estimated_tokens: u64,
    summary_used: bool,
    summary: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ApprovalRequest {
    pub approval_id: String,
    pub turn_id: String,
    pub call_id: String,
    pub tool: String,
    pub action: String,
    pub resource: String,
    pub args: Value,
    pub reason: String,
}

pub trait EventSink: Send + Sync {
    fn emit(&self, event: CoreEvent) -> Result<(), AgentError>;
}

pub trait ApprovalGate: Send + Sync {
    fn request(&self, request: ApprovalRequest) -> ApprovalDecision;
}

#[derive(Debug, thiserror::Error)]
pub enum AgentError {
    #[error("provider error: {0}")]
    Provider(#[from] ProviderError),
    #[error("event sink error: {0}")]
    Event(String),
    #[error("audit error: {0}")]
    Audit(String),
    #[error("tool error: {0}")]
    Tool(String),
    #[error("maximum iterations reached")]
    MaxIterations,
    #[error("token budget exceeded")]
    TokenBudgetExceeded,
    #[error("context window exceeded: {0}")]
    ContextWindowExceeded(String),
}

enum ProviderMessage {
    Event(ChatEvent),
    Done(Result<ChatOutcome, ProviderError>),
}

/// Explicit agent-loop states. Terminal transitions (completion, cancellation,
/// failure and max iterations) return from the loop directly.
#[derive(Debug)]
enum TurnState {
    Model {
        iteration: usize,
    },
    Tools {
        iteration: usize,
        calls: Vec<ToolCall>,
        index: usize,
    },
}

pub struct Agent {
    provider: Arc<dyn Provider>,
    metadata: ModelMetadata,
    tools: Arc<ToolRegistry>,
    policy: Arc<PolicyEngine>,
    sandbox: Arc<SandboxManager>,
    audit: Arc<dyn AuditSink>,
    config: AgentConfig,
    retry: RetryPolicy,
    system_prompt: String,
}

impl Agent {
    pub fn new(
        provider: Arc<dyn Provider>,
        metadata: ModelMetadata,
        tools: Arc<ToolRegistry>,
        policy: Arc<PolicyEngine>,
        sandbox: Arc<SandboxManager>,
        audit: Arc<dyn AuditSink>,
        config: AgentConfig,
    ) -> Self {
        let system_prompt = system_prompt(policy.workspace());
        Self {
            provider,
            metadata,
            tools,
            policy,
            sandbox,
            audit,
            config,
            retry: RetryPolicy::default(),
            system_prompt,
        }
    }

    pub fn metadata(&self) -> &ModelMetadata {
        &self.metadata
    }

    /// Appends extra instructions to the system prompt.
    pub fn with_system_prompt(mut self, extra: &str) -> Self {
        let extra = extra.trim();
        if !extra.is_empty() {
            self.system_prompt.push_str("\n\n");
            self.system_prompt.push_str(extra);
            self.system_prompt.push('\n');
        }
        self
    }

    /// Compacts a session history to fit the context budget.
    ///
    /// Rounds that no longer fit are replaced by a model written summary
    /// while the most recent rounds stay verbatim. When the provider call
    /// fails the mechanical trim is used instead, so a session is never lost.
    pub fn compact_history(&self, history: &[ChatMessage]) -> Result<CompactionResult, AgentError> {
        let request = self.request_for(history);
        if !history.iter().any(|message| message.role == Role::User) {
            return Ok(CompactionResult {
                history: history.to_vec(),
                omitted_messages: 0,
                estimated_tokens: estimate_history_tokens(
                    &self.system_prompt,
                    &request.tools,
                    history,
                ),
                summary_used: false,
                summary: None,
            });
        }
        let budget = effective_context_budget(&self.metadata);
        let mechanical = compact_history(history, &self.system_prompt, &request.tools, budget);
        // The mechanical trim drops whole rounds and leaves a marker behind.
        // That loses the content, so the model is asked to summarize them.
        let loses_rounds = match &mechanical {
            None => true,
            Some(compacted) => compacted
                .iter()
                .any(|message| compaction_omitted(message).is_some()),
        };
        if loses_rounds {
            if let Some((compacted, summary, omitted)) =
                self.summarize_history(history, &request.tools, budget)
            {
                let estimated_tokens =
                    estimate_history_tokens(&self.system_prompt, &request.tools, &compacted);
                return Ok(CompactionResult {
                    history: compacted,
                    omitted_messages: omitted,
                    estimated_tokens,
                    summary_used: true,
                    summary: Some(summary),
                });
            }
        }
        let compacted = mechanical.ok_or_else(|| {
            AgentError::ContextWindowExceeded(format!(
                "history cannot be compacted within budget {budget}"
            ))
        })?;
        let previous_omitted = history
            .iter()
            .filter_map(compaction_omitted)
            .fold(0usize, usize::saturating_add);
        let total_omitted = compacted
            .iter()
            .filter_map(compaction_omitted)
            .fold(0usize, usize::saturating_add);
        let estimated_tokens =
            estimate_history_tokens(&self.system_prompt, &request.tools, &compacted);
        Ok(CompactionResult {
            history: compacted,
            omitted_messages: total_omitted.saturating_sub(previous_omitted),
            estimated_tokens,
            summary_used: false,
            summary: None,
        })
    }

    /// Replaces the oldest rounds with a model written summary and keeps the
    /// most recent rounds verbatim. Returns None when the provider cannot be
    /// reached or when the summarized history would not fit the budget, so
    /// the caller can fall back to the mechanical trim.
    fn summarize_history(
        &self,
        history: &[ChatMessage],
        tools: &[ToolDefinition],
        budget: u64,
    ) -> Option<(Vec<ChatMessage>, String, usize)> {
        let mut systems: Vec<ChatMessage> = Vec::new();
        let mut previous_summaries: Vec<String> = Vec::new();
        let mut conversation: Vec<ChatMessage> = Vec::new();
        for message in history {
            if compaction_omitted(message).is_some() {
                continue;
            }
            match message.role {
                Role::System if message.text().starts_with(SUMMARY_HEADER) => {
                    previous_summaries.push(message.text().to_owned());
                }
                Role::System => systems.push(message.clone()),
                _ => conversation.push(message.clone()),
            }
        }
        let rounds = split_rounds(&conversation);
        if rounds.len() < 2 {
            return None;
        }
        let summary_tokens = (budget / 4).clamp(64, SUMMARY_MAX_OUTPUT_TOKENS);
        let allowance = summary_tokens.saturating_add(SUMMARY_TOKEN_MARGIN);
        let max_keep = rounds
            .len()
            .saturating_sub(1)
            .min(SUMMARY_KEEP_RECENT_ROUNDS);
        let mut keep = 0usize;
        let mut tail: Vec<ChatMessage> = Vec::new();
        for candidate in 1..=max_keep {
            let candidate_tail: Vec<ChatMessage> = rounds[rounds.len() - candidate..]
                .iter()
                .flatten()
                .cloned()
                .collect();
            let mut probe = systems.clone();
            probe.extend(candidate_tail.iter().cloned());
            let tokens = estimate_history_tokens(&self.system_prompt, tools, &probe);
            if tokens.saturating_add(allowance) <= budget {
                keep = candidate;
                tail = candidate_tail;
            } else {
                break;
            }
        }
        if keep == 0 {
            return None;
        }
        let old = &rounds[..rounds.len() - keep];
        let dropped: usize = old.iter().map(|round| round.len()).sum();
        let previously_omitted = history
            .iter()
            .filter_map(compaction_omitted)
            .fold(0usize, usize::saturating_add);
        let mut transcript = String::new();
        for previous in &previous_summaries {
            transcript.push_str(previous);
            transcript.push('\n');
        }
        transcript.push_str(&render_rounds(old));
        let summary = self.request_summary(&transcript, summary_tokens)?;
        let summary =
            truncate_chars_keep_ends(&summary, (summary_tokens as usize).saturating_mul(3));
        let mut compacted = systems;
        compacted.push(ChatMessage::system(format!("{SUMMARY_HEADER}\n{summary}")));
        if previously_omitted.saturating_add(dropped) > 0 {
            compacted.push(compaction_marker(
                previously_omitted.saturating_add(dropped),
            ));
        }
        compacted.extend(tail);
        if estimate_history_tokens(&self.system_prompt, tools, &compacted) >= budget {
            return None;
        }
        Some((compacted, summary, dropped))
    }

    /// Asks the model for a summary of the older rounds. The request is not
    /// part of the conversation and never reaches the user interface.
    fn request_summary(&self, transcript: &str, max_tokens: u64) -> Option<String> {
        let input_budget = u64::from(self.metadata.context_window)
            .saturating_sub(u64::from(self.metadata.max_output_tokens))
            .saturating_sub(max_tokens)
            .saturating_sub(SUMMARY_TOKEN_MARGIN);
        // shortcut: a transcript longer than one context window is summarized
        // from its head and tail only, upgrade to chunked summaries when
        // sessions regularly exceed the window.
        let transcript =
            truncate_chars_keep_ends(transcript, (input_budget as usize).saturating_mul(3));
        let mut request = ChatRequest::new(
            self.metadata.model.clone(),
            vec![ChatMessage::user(transcript)],
        );
        request.system = SUMMARY_INSTRUCTIONS.to_owned();
        request.max_tokens = max_tokens.min(u32::MAX as u64) as u32;
        let cancel = CancellationToken::new();
        let mut ignore = |_event: ChatEvent| {};
        let outcome = self
            .provider
            .stream_chat(&request, &cancel, &mut ignore)
            .ok()?;
        let text = outcome.text.trim();
        if text.is_empty() {
            return None;
        }
        Some(text.to_owned())
    }

    #[allow(clippy::too_many_arguments)]
    pub fn run_turn(
        &self,
        session_id: &str,
        turn_id: &str,
        prompt: &str,
        history: &mut Vec<ChatMessage>,
        cancel: Arc<AtomicBool>,
        sink: &dyn EventSink,
        approvals: &dyn ApprovalGate,
    ) -> Result<TurnResult, AgentError> {
        self.provider.begin_turn();
        let mut usage = Usage::default();
        history.push(ChatMessage::user(prompt));
        sink.emit(CoreEvent::TurnStarted {
            turn_id: turn_id.to_owned(),
        })?;

        let mut state = TurnState::Model { iteration: 0 };
        loop {
            if cancel.load(Ordering::Relaxed) {
                return self.finish_cancelled(turn_id, history, sink, usage);
            }

            state = match state {
                TurnState::Model { iteration } => {
                    if iteration >= self.config.max_iterations {
                        let message = format!(
                            "agent stopped after {} iterations without a final answer",
                            self.config.max_iterations
                        );
                        sink.emit(CoreEvent::TurnFailed {
                            turn_id: turn_id.to_owned(),
                            message: message.clone(),
                        })?;
                        return Err(AgentError::MaxIterations);
                    }

                    let (request, compaction) = self.prepare_request(history)?;
                    if let Some(compaction) = compaction {
                        sink.emit(CoreEvent::ContextCompacted {
                            trigger: "turn.auto".to_owned(),
                            omitted_messages: compaction.omitted_messages,
                            estimated_tokens: compaction.estimated_tokens,
                            summary_used: compaction.summary_used,
                            summary: compaction.summary,
                        })?;
                    }

                    let estimated_input = estimate_request_tokens(&request);
                    let outcome = match self.stream_provider(request, cancel.clone(), sink, turn_id)
                    {
                        Ok(outcome) => outcome,
                        Err(AgentError::Provider(error))
                            if error.kind == ProviderErrorKind::Cancelled =>
                        {
                            return self.finish_cancelled(turn_id, history, sink, usage);
                        }
                        Err(error) => {
                            let _ = sink.emit(CoreEvent::TurnFailed {
                                turn_id: turn_id.to_owned(),
                                message: error.to_string(),
                            });
                            return Err(error);
                        }
                    };

                    if outcome.usage.input_tokens + outcome.usage.output_tokens == 0 {
                        usage.input_tokens += estimated_input;
                        usage.output_tokens += estimate_outcome_tokens(&outcome);
                    } else {
                        usage.input_tokens += outcome.usage.input_tokens;
                        usage.output_tokens += outcome.usage.output_tokens;
                    }
                    if self.config.max_total_tokens > 0
                        && usage.input_tokens + usage.output_tokens > self.config.max_total_tokens
                    {
                        let message = format!(
                            "token budget exceeded: {} > {}",
                            usage.input_tokens + usage.output_tokens,
                            self.config.max_total_tokens
                        );
                        sink.emit(CoreEvent::TurnFailed {
                            turn_id: turn_id.to_owned(),
                            message: message.clone(),
                        })?;
                        return Err(AgentError::TokenBudgetExceeded);
                    }
                    if cancel.load(Ordering::Relaxed) {
                        return self.finish_cancelled(turn_id, history, sink, usage);
                    }

                    if outcome.tool_calls.is_empty() {
                        history.push(ChatMessage::assistant_with_text(
                            outcome.text.clone(),
                            Vec::new(),
                        ));
                        let cost_usd = usage_cost_usd(&self.metadata, &usage);
                        sink.emit(CoreEvent::TurnCompleted {
                            turn_id: turn_id.to_owned(),
                            status: TurnStatus::Completed.as_str().to_owned(),
                            text: outcome.text.clone(),
                            input_tokens: usage.input_tokens,
                            output_tokens: usage.output_tokens,
                            cost_usd,
                        })?;
                        return Ok(TurnResult {
                            status: TurnStatus::Completed,
                            text: outcome.text,
                            usage,
                        });
                    }

                    if outcome.tool_calls.len() > self.config.max_tool_calls_per_turn {
                        let message = format!(
                            "tool call limit exceeded: {} > {}",
                            outcome.tool_calls.len(),
                            self.config.max_tool_calls_per_turn
                        );
                        sink.emit(CoreEvent::TurnFailed {
                            turn_id: turn_id.to_owned(),
                            message: message.clone(),
                        })?;
                        return Err(AgentError::Tool(message));
                    }
                    let calls = outcome.tool_calls.clone();
                    history.push(ChatMessage::assistant_with_text(
                        outcome.text,
                        outcome.tool_calls,
                    ));
                    TurnState::Tools {
                        iteration,
                        calls,
                        index: 0,
                    }
                }
                TurnState::Tools {
                    iteration,
                    calls,
                    index,
                } => {
                    if index == 0 && calls.len() > 1 && self.can_parallelize(&calls) {
                        let results = self.execute_tool_batch(
                            session_id, turn_id, &calls, &cancel, sink, approvals,
                        );
                        for (call, result) in calls.iter().zip(results) {
                            history.push(ChatMessage::tool(&call.id, result));
                        }
                        if cancel.load(Ordering::Relaxed) {
                            return self.finish_cancelled(turn_id, history, sink, usage);
                        }
                        TurnState::Model {
                            iteration: iteration + 1,
                        }
                    } else if index >= calls.len() {
                        TurnState::Model {
                            iteration: iteration + 1,
                        }
                    } else {
                        let call = &calls[index];
                        let result = self
                            .execute_tool_call(session_id, turn_id, call, &cancel, sink, approvals);
                        history.push(ChatMessage::tool(&call.id, result));
                        if cancel.load(Ordering::Relaxed) {
                            return self.finish_cancelled(turn_id, history, sink, usage);
                        }
                        TurnState::Tools {
                            iteration,
                            calls,
                            index: index + 1,
                        }
                    }
                }
            };
        }
    }

    fn stream_provider(
        &self,
        request: ChatRequest,
        cancel: Arc<AtomicBool>,
        sink: &dyn EventSink,
        turn_id: &str,
    ) -> Result<ChatOutcome, AgentError> {
        let (sender, receiver) = mpsc::channel();
        let provider = self.provider.clone();
        let token = CancellationToken::from_arc(cancel.clone());
        let worker_token = token.clone();
        let retry = self.retry;
        let mut worker = Some(std::thread::spawn(move || {
            let result = stream_with_retry(
                provider.as_ref(),
                &request,
                &worker_token,
                &retry,
                &mut |event| {
                    let _ = sender.send(ProviderMessage::Event(event));
                },
            );
            let _ = sender.send(ProviderMessage::Done(result));
        }));

        loop {
            if cancel.load(Ordering::Relaxed) {
                token.cancel();
                if !join_worker(&mut worker, Duration::from_secs(2)) {
                    return Err(AgentError::Provider(ProviderError::new(
                        ProviderErrorKind::Transport,
                        "provider worker did not stop within 2 seconds",
                    )));
                }
                return Err(AgentError::Provider(ProviderError::cancelled()));
            }
            match receiver.recv_timeout(Duration::from_millis(100)) {
                Ok(ProviderMessage::Event(event)) => {
                    let mapped = match event {
                        ChatEvent::TextDelta(text) => Some(CoreEvent::TurnDelta {
                            turn_id: turn_id.to_owned(),
                            text,
                        }),
                        ChatEvent::ReasoningDelta(text) => Some(CoreEvent::TurnReasoning {
                            turn_id: turn_id.to_owned(),
                            text,
                        }),
                        ChatEvent::ToolCallDelta { .. } | ChatEvent::Usage(_) => None,
                    };
                    if let Some(mapped) = mapped {
                        sink.emit(mapped)?;
                    }
                }
                Ok(ProviderMessage::Done(Ok(outcome))) => {
                    join_worker(&mut worker, Duration::from_secs(1));
                    return Ok(outcome);
                }
                Ok(ProviderMessage::Done(Err(error))) => {
                    join_worker(&mut worker, Duration::from_secs(1));
                    if cancel.load(Ordering::Relaxed) {
                        return Err(AgentError::Provider(ProviderError::cancelled()));
                    }
                    return Err(error.into());
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    token.cancel();
                    join_worker(&mut worker, Duration::from_secs(2));
                    return Err(AgentError::Provider(ProviderError::new(
                        ProviderErrorKind::Transport,
                        "provider stream disconnected",
                    )));
                }
            }
        }
    }

    fn request_for(&self, history: &[ChatMessage]) -> ChatRequest {
        // The compaction summary is a system message, but some providers
        // ignore system messages inside the message list, so it is folded
        // into the system prompt.
        let mut system = self.system_prompt.clone();
        let mut messages = Vec::with_capacity(history.len());
        for message in history {
            if message.role == Role::System && message.text().starts_with(SUMMARY_HEADER) {
                if !system.is_empty() {
                    system.push_str("\n\n");
                }
                system.push_str(&message.text());
            } else {
                messages.push(message.clone());
            }
        }
        let mut request = ChatRequest::new(self.metadata.model.clone(), messages);
        request.max_tokens = self.metadata.max_output_tokens;
        request.system = system;
        request.tools = self.tools.definitions();
        request.reasoning_effort = self.metadata.reasoning_effort.clone();
        request
    }

    fn prepare_request(
        &self,
        history: &mut Vec<ChatMessage>,
    ) -> Result<(ChatRequest, Option<CompactionNotice>), AgentError> {
        let request = self.request_for(history);
        let budget = effective_context_budget(&self.metadata);
        let estimated = estimate_context_tokens(&request);
        if estimated <= budget {
            return Ok((request, None));
        }

        let compacted = self.compact_history(history)?;
        let request = self.request_for(&compacted.history);
        if compacted.estimated_tokens >= budget {
            return Err(AgentError::ContextWindowExceeded(format!(
                "compacted request uses {} input tokens, budget is {budget}",
                compacted.estimated_tokens
            )));
        }
        let notice = CompactionNotice {
            omitted_messages: compacted.omitted_messages,
            estimated_tokens: compacted.estimated_tokens,
            summary_used: compacted.summary_used,
            summary: compacted.summary,
        };
        *history = compacted.history;
        Ok((request, Some(notice)))
    }

    fn finish_cancelled(
        &self,
        turn_id: &str,
        history: &mut Vec<ChatMessage>,
        sink: &dyn EventSink,
        usage: Usage,
    ) -> Result<TurnResult, AgentError> {
        history.push(ChatMessage::assistant_with_text(
            "Cancelled by user.",
            Vec::new(),
        ));
        sink.emit(CoreEvent::TurnCompleted {
            turn_id: turn_id.to_owned(),
            status: TurnStatus::Cancelled.as_str().to_owned(),
            text: "Cancelled by user.".to_owned(),
            input_tokens: usage.input_tokens,
            output_tokens: usage.output_tokens,
            cost_usd: usage_cost_usd(&self.metadata, &usage),
        })?;
        Ok(TurnResult {
            status: TurnStatus::Cancelled,
            text: "Cancelled by user.".to_owned(),
            usage,
        })
    }

    fn can_parallelize(&self, calls: &[ToolCall]) -> bool {
        calls.iter().all(|call| {
            let Ok(args) = serde_json::from_str::<Value>(&call.arguments) else {
                return false;
            };
            self.tools
                .get(&call.name)
                .is_some_and(|tool| tool.concurrency_safe(&args))
        })
    }

    #[allow(clippy::too_many_arguments)]
    fn execute_tool_batch(
        &self,
        session_id: &str,
        turn_id: &str,
        calls: &[ToolCall],
        cancel: &AtomicBool,
        sink: &dyn EventSink,
        approvals: &dyn ApprovalGate,
    ) -> Vec<String> {
        const MAX_WORKERS: usize = 10;
        let results: Vec<Mutex<Option<String>>> =
            (0..calls.len()).map(|_| Mutex::new(None)).collect();
        let next = std::sync::atomic::AtomicUsize::new(0);
        std::thread::scope(|scope| {
            for _ in 0..calls.len().min(MAX_WORKERS) {
                scope.spawn(|| {
                    loop {
                        let index = next.fetch_add(1, Ordering::Relaxed);
                        let Some(call) = calls.get(index) else {
                            break;
                        };
                        let result = self
                            .execute_tool_call(session_id, turn_id, call, cancel, sink, approvals);
                        *results[index]
                            .lock()
                            .unwrap_or_else(|error| error.into_inner()) = Some(result);
                    }
                });
            }
        });
        results
            .into_iter()
            .map(|result| {
                result
                    .into_inner()
                    .unwrap_or_else(|error| error.into_inner())
                    .unwrap_or_else(|| error_payload("parallel tool did not run"))
            })
            .collect()
    }

    #[allow(clippy::too_many_arguments)]
    fn execute_tool_call(
        &self,
        session_id: &str,
        turn_id: &str,
        call: &ToolCall,
        cancel: &AtomicBool,
        sink: &dyn EventSink,
        approvals: &dyn ApprovalGate,
    ) -> String {
        let args: Value = match serde_json::from_str(&call.arguments) {
            Ok(args) => args,
            Err(error) => {
                let message = format!("invalid JSON arguments: {error}");
                if let Err(audit_error) = self.record_failure(
                    session_id,
                    turn_id,
                    call,
                    "invalid",
                    "",
                    "invalid",
                    &Value::Null,
                    &message,
                    sink,
                ) {
                    return error_payload(&audit_error.to_string());
                }
                return error_payload(&message);
            }
        };

        let Some(tool) = self.tools.get(&call.name) else {
            let message = format!("unknown tool: {}", call.name);
            if let Err(audit_error) = self.record_failure(
                session_id, turn_id, call, "unknown", "", "unknown", &args, &message, sink,
            ) {
                return error_payload(&audit_error.to_string());
            }
            return error_payload(&message);
        };

        if let Err(error) = tool.validate(&args) {
            let message = error.to_string();
            if let Err(audit_error) = self.record_failure(
                session_id,
                turn_id,
                call,
                tool.name(),
                "invalid",
                "invalid",
                &args,
                &message,
                sink,
            ) {
                return error_payload(&audit_error.to_string());
            }
            return error_payload(&message);
        }

        let request: PolicyRequest = match tool.policy_request_for(self.policy.workspace(), &args) {
            Ok(request) => request,
            Err(error) => {
                let message = error.to_string();
                if let Err(audit_error) = self.record_failure(
                    session_id,
                    turn_id,
                    call,
                    tool.name(),
                    "invalid",
                    "invalid",
                    &args,
                    &message,
                    sink,
                ) {
                    return error_payload(&audit_error.to_string());
                }
                return error_payload(&message);
            }
        };

        let evaluation = self.policy.evaluate_with_access(
            self.config.tool_access,
            request.action,
            &request.resource,
        );
        let audit_resource = evaluation
            .resolved
            .as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_else(|| request.resource.clone());
        let mut audit = AuditRecord::new(
            session_id,
            turn_id,
            call.id.clone(),
            tool.name(),
            request.action.as_str(),
            &audit_resource,
            evaluation.effect.as_str(),
        );
        audit.with_args(&args);

        let mut approved: Option<bool> = None;
        if evaluation.effect == Effect::Deny {
            let message = format!("policy denied: {}", evaluation.reason);
            let _ = sink.emit(CoreEvent::SandboxViolation {
                turn_id: turn_id.to_owned(),
                call_id: call.id.clone(),
                kind: "policy_denied".to_owned(),
                resource: audit_resource.clone(),
                message: message.clone(),
            });
            audit.ok = false;
            audit.error = Some(message.clone());
            audit.with_output("");
            if let Err(error) = self.emit_audit(&audit, turn_id, sink) {
                return error_payload(&error.to_string());
            }
            let _ = sink.emit(CoreEvent::ToolCompleted {
                turn_id: turn_id.to_owned(),
                call_id: call.id.clone(),
                name: call.name.clone(),
                ok: false,
                summary: message.clone(),
                output: None,
                diff: None,
            });
            return error_payload(&message);
        }

        if evaluation.effect == Effect::Ask || tool.always_ask() {
            let reason = if evaluation.effect == Effect::Ask {
                evaluation.reason.clone()
            } else {
                "user interaction requires approval".to_owned()
            };
            let approval_id = format!("{turn_id}:{}", call.id);
            let decision = approvals.request(ApprovalRequest {
                approval_id,
                turn_id: turn_id.to_owned(),
                call_id: call.id.clone(),
                tool: call.name.clone(),
                action: request.action.as_str().to_owned(),
                resource: request.resource.clone(),
                args: args.clone(),
                reason,
            });
            approved = Some(decision == ApprovalDecision::Allow);
            if decision == ApprovalDecision::Deny {
                let message = "approval denied by user".to_owned();
                audit.approved = Some(false);
                audit.ok = false;
                audit.error = Some(message.clone());
                audit.with_output("");
                if let Err(error) = self.emit_audit(&audit, turn_id, sink) {
                    return error_payload(&error.to_string());
                }
                let _ = sink.emit(CoreEvent::ToolCompleted {
                    turn_id: turn_id.to_owned(),
                    call_id: call.id.clone(),
                    name: call.name.clone(),
                    ok: false,
                    summary: message.clone(),
                    output: None,
                    diff: None,
                });
                return error_payload(&message);
            }
        }
        audit.approved = approved;

        if cancel.load(Ordering::Relaxed) {
            audit.ok = false;
            audit.error = Some("cancelled".to_owned());
            audit.with_output("");
            let _ = self.emit_audit(&audit, turn_id, sink);
            return error_payload("cancelled");
        }

        let _ = sink.emit(CoreEvent::ToolStarted {
            turn_id: turn_id.to_owned(),
            call_id: call.id.clone(),
            name: call.name.clone(),
            args: args.clone(),
        });

        let started = Instant::now();
        let context = ToolContext {
            policy: &self.policy,
            cancel,
            sandbox: self.sandbox.clone(),
            timeout: self.config.command_timeout,
            max_output_bytes: self.config.max_tool_output_bytes,
        };
        let outcome = tool.execute(&context, &args);
        audit.duration_ms = started.elapsed().as_millis() as u64;

        match outcome {
            Ok(outcome) => {
                audit.ok = outcome.success;
                let output = truncate_chars_keep_ends(
                    &truncate(&outcome.output, self.config.max_tool_output_bytes),
                    tool.max_result_chars(),
                );
                audit.with_output(&output);
                if let Err(error) = self.emit_audit(&audit, turn_id, sink) {
                    return error_payload(&error.to_string());
                }
                if let Err(error) = self.emit_tool_output(turn_id, &call.id, &output, sink) {
                    return error_payload(&format!("event sink error: {error}"));
                }
                let _ = sink.emit(CoreEvent::ToolCompleted {
                    turn_id: turn_id.to_owned(),
                    call_id: call.id.clone(),
                    name: call.name.clone(),
                    ok: outcome.success,
                    summary: outcome.summary.clone(),
                    output: Some(truncate(&output, 8192)),
                    diff: outcome.diff.clone(),
                });
                result_payload(
                    outcome.success,
                    &outcome.summary,
                    &output,
                    outcome.diff.as_deref(),
                )
            }
            Err(error) => {
                let message = error.to_string();
                let kind = match &error {
                    ToolError::Sandbox(violation) => Some(violation.kind.as_str().to_owned()),
                    ToolError::Policy(_) => Some("policy_denied".to_owned()),
                    _ => None,
                };
                if let Some(kind) = kind {
                    let _ = sink.emit(CoreEvent::SandboxViolation {
                        turn_id: turn_id.to_owned(),
                        call_id: call.id.clone(),
                        kind,
                        resource: args
                            .get("path")
                            .and_then(Value::as_str)
                            .unwrap_or(&call.name)
                            .to_owned(),
                        message: message.clone(),
                    });
                }
                audit.ok = false;
                audit.error = Some(message.clone());
                audit.with_output("");
                if let Err(audit_error) = self.emit_audit(&audit, turn_id, sink) {
                    return error_payload(&audit_error.to_string());
                }
                let _ = sink.emit(CoreEvent::ToolCompleted {
                    turn_id: turn_id.to_owned(),
                    call_id: call.id.clone(),
                    name: call.name.clone(),
                    ok: false,
                    summary: message.clone(),
                    output: None,
                    diff: None,
                });
                error_payload(&message)
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn record_failure(
        &self,
        session_id: &str,
        turn_id: &str,
        call: &ToolCall,
        tool_name: &str,
        action: &str,
        decision: &str,
        args: &Value,
        message: &str,
        sink: &dyn EventSink,
    ) -> Result<(), String> {
        let mut audit = AuditRecord::new(
            session_id,
            turn_id,
            call.id.clone(),
            tool_name,
            action,
            call.name.clone(),
            decision,
        );
        audit.with_args(args);
        audit.ok = false;
        audit.error = Some(message.to_owned());
        audit.with_output("");
        self.emit_audit(&audit, turn_id, sink)
            .map_err(|error| error.to_string())?;
        let _ = sink.emit(CoreEvent::ToolCompleted {
            turn_id: turn_id.to_owned(),
            call_id: call.id.clone(),
            name: call.name.clone(),
            ok: false,
            summary: message.to_owned(),
            output: None,
            diff: None,
        });
        Ok(())
    }

    fn emit_audit(
        &self,
        audit: &AuditRecord,
        turn_id: &str,
        sink: &dyn EventSink,
    ) -> Result<(), AgentError> {
        self.audit
            .record(audit)
            .map_err(|error| AgentError::Audit(error.to_string()))?;
        sink.emit(CoreEvent::AuditRecorded {
            turn_id: Some(turn_id.to_owned()),
            record_id: audit.id.clone(),
        })?;
        Ok(())
    }

    fn emit_tool_output(
        &self,
        turn_id: &str,
        call_id: &str,
        output: &str,
        sink: &dyn EventSink,
    ) -> Result<(), AgentError> {
        if output.is_empty() {
            return Ok(());
        }
        for chunk in output.as_bytes().chunks(8192) {
            let text = String::from_utf8_lossy(chunk).into_owned();
            sink.emit(CoreEvent::ToolOutput {
                turn_id: turn_id.to_owned(),
                call_id: call_id.to_owned(),
                stream: "combined".to_owned(),
                chunk: text,
            })?;
        }
        Ok(())
    }
}

fn join_worker(worker: &mut Option<std::thread::JoinHandle<()>>, timeout: Duration) -> bool {
    let started = Instant::now();
    loop {
        let Some(handle) = worker.as_ref() else {
            return true;
        };
        if handle.is_finished() {
            let handle = worker.take().expect("worker exists");
            let _ = handle.join();
            return true;
        }
        if started.elapsed() >= timeout {
            return false;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

/// Runs one provider attempt and retries transient failures with exponential
/// backoff. A retry is skipped once the attempt streamed a delta, so a
/// partially delivered answer is never duplicated.
fn stream_with_retry(
    provider: &dyn Provider,
    request: &ChatRequest,
    cancel: &CancellationToken,
    policy: &RetryPolicy,
    on_event: &mut dyn FnMut(ChatEvent),
) -> Result<ChatOutcome, ProviderError> {
    let mut attempt = 0u32;
    loop {
        let mut emitted_delta = false;
        let result = {
            let mut forward = |event: ChatEvent| {
                if is_delta(&event) {
                    emitted_delta = true;
                }
                on_event(event);
            };
            provider.stream_chat(request, cancel, &mut forward)
        };

        let error = match result {
            Ok(outcome) => return Ok(outcome),
            Err(error) => error,
        };
        // A streamed delta is already on its way to the sink, so retrying
        // would duplicate output.
        if emitted_delta
            || !error.retryable
            || error.kind == ProviderErrorKind::Cancelled
            || attempt + 1 >= policy.max_attempts
        {
            return Err(error);
        }
        if !sleep_cancellable(cancel, policy.delay(attempt)) {
            return Err(ProviderError::cancelled());
        }
        attempt += 1;
    }
}

/// True for events that carry model output to the sink.
fn is_delta(event: &ChatEvent) -> bool {
    matches!(
        event,
        ChatEvent::TextDelta(_) | ChatEvent::ReasoningDelta(_) | ChatEvent::ToolCallDelta { .. }
    )
}

/// Sleeps for `total`, returning false as soon as the wait is cancelled.
fn sleep_cancellable(cancel: &CancellationToken, total: Duration) -> bool {
    let deadline = Instant::now() + total;
    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return !cancel.is_cancelled();
        }
        if cancel.is_cancelled() {
            return false;
        }
        std::thread::sleep(remaining.min(Duration::from_millis(20)));
    }
}

/// Random wait in `[0, cap]`.
fn jitter(cap: Duration) -> Duration {
    let span = cap.as_nanos() as u64;
    if span == 0 {
        return Duration::ZERO;
    }
    let mut state = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos() as u64)
        .unwrap_or(0x9E37_79B9_7F4A_7C15);
    // xorshift64 keeps the jitter cheap and dependency-free.
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    Duration::from_nanos(state % span)
}

const AGENTS_MD_LIMIT: usize = 16 * 1024;

fn read_agents_md(path: &std::path::Path) -> Option<String> {
    let metadata = std::fs::symlink_metadata(path).ok()?;
    if !metadata.file_type().is_file() {
        return None;
    }
    let mut content = std::fs::read_to_string(path).ok()?;
    if content.len() > AGENTS_MD_LIMIT {
        let mut end = AGENTS_MD_LIMIT;
        while !content.is_char_boundary(end) {
            end -= 1;
        }
        content.truncate(end);
        content.push_str("\n[AGENTS.md truncated]");
    }
    Some(content)
}

/// Global instructions from `<home>/AGENTS.md`. Core reads them on every turn,
/// so an edit applies to the next message.
pub fn home_instructions(home: &std::path::Path) -> Option<String> {
    read_agents_md(&home.join("AGENTS.md"))
}

fn system_prompt(workspace: &std::path::Path) -> String {
    let mut prompt = format!(
        "You are KL, a terminal coding agent. Work only inside the workspace at {}.\n\
         Inspect before editing. Use read_file, grep and glob to gather evidence; use edit_file \
         for exact replacements and write_file for new files. Use shell for builds/tests and git \
         for repository operations. Keep changes minimal, match existing style, and verify with \
         tests when practical. Never claim success without evidence.",
        workspace.display()
    );
    let Some(content) = read_agents_md(&workspace.join("AGENTS.md")) else {
        return prompt;
    };
    prompt.push_str("\n\nProject instructions from AGENTS.md:\n");
    prompt.push_str(&content);
    prompt.push('\n');
    prompt
}

fn result_payload(ok: bool, summary: &str, output: &str, diff: Option<&str>) -> String {
    let mut payload = json!({
        "ok": ok,
        "summary": summary,
        "output": output,
    });
    if let Some(diff) = diff {
        payload["diff"] = json!(diff);
    }
    truncate(
        &serde_json::to_string(&payload).unwrap_or_else(|_| "{\"ok\":false}".to_owned()),
        48 * 1024,
    )
}

fn error_payload(message: &str) -> String {
    serde_json::to_string(&json!({"ok": false, "error": message}))
        .unwrap_or_else(|_| "{\"ok\":false}".to_owned())
}

const MESSAGE_OVERHEAD_TOKENS: u64 = 8;
const COMPACTION_MARKER_PREFIX: &str = "[KL context compacted: omitted ";
const COMPACTION_MARKER_SUFFIX: &str = " older message(s)]";
const TOOL_PRUNE_MARKER_PREFIX: &str = "[KL tool result pruned: ";
const TOOL_PRUNE_MARKER_SUFFIX: &str = " chars]";
const TOOL_PRUNE_THRESHOLD: usize = 2 * 1024;
const SUMMARY_HEADER: &str = "[KL conversation summary]";
const SUMMARY_INSTRUCTIONS: &str = "Summarize the earlier part of a coding session. Keep decisions, file paths, commands, results and open questions. Reply with the summary only.";
const SUMMARY_KEEP_RECENT_ROUNDS: usize = 2;
const SUMMARY_MAX_OUTPUT_TOKENS: u64 = 1024;
const SUMMARY_TOKEN_MARGIN: u64 = 64;

fn effective_context_budget(metadata: &ModelMetadata) -> u64 {
    let window = u64::from(metadata.context_window);
    let threshold = f64::from(metadata.compact_threshold).clamp(0.0, 1.0);
    let threshold_budget = (window as f64 * threshold).floor() as u64;
    let output_budget = window.saturating_sub(u64::from(metadata.max_output_tokens));
    threshold_budget.min(output_budget).max(1)
}

fn compact_history(
    history: &[ChatMessage],
    system: &str,
    tools: &[ToolDefinition],
    budget: u64,
) -> Option<Vec<ChatMessage>> {
    let systems = history
        .iter()
        .filter(|message| message.role == Role::System)
        .cloned()
        .collect::<Vec<_>>();
    let previously_omitted = history
        .iter()
        .filter_map(compaction_omitted)
        .fold(0usize, usize::saturating_add);
    let mut conversation = history
        .iter()
        .filter(|message| message.role != Role::System && compaction_omitted(message).is_none())
        .cloned()
        .collect::<Vec<_>>();
    let mut pruned = Vec::with_capacity(systems.len() + conversation.len());
    pruned.extend(systems.iter().cloned());
    pruned.extend(conversation.iter().cloned());
    if estimate_history_tokens(system, tools, &pruned) < budget {
        return Some(pruned);
    }
    prune_tool_results(&mut conversation);
    pruned.clear();
    pruned.extend(systems.iter().cloned());
    pruned.extend(conversation.iter().cloned());
    if estimate_history_tokens(system, tools, &pruned) < budget {
        return Some(pruned);
    }
    let round_starts = conversation
        .iter()
        .enumerate()
        .filter_map(|(index, message)| (message.role == Role::User).then_some(index))
        .collect::<Vec<_>>();
    if round_starts.is_empty() {
        return None;
    }

    for start in round_starts.into_iter().rev() {
        let omitted = previously_omitted.saturating_add(start);
        let mut candidate = Vec::with_capacity(systems.len() + 1 + conversation.len() - start);
        candidate.extend(systems.iter().cloned());
        if omitted > 0 {
            candidate.push(compaction_marker(omitted));
        }
        candidate.extend(conversation[start..].iter().cloned());
        if estimate_history_tokens(system, tools, &candidate) < budget {
            return Some(candidate);
        }
    }
    None
}

fn estimate_history_tokens(system: &str, tools: &[ToolDefinition], history: &[ChatMessage]) -> u64 {
    let mut request = ChatRequest::new("", history.to_vec());
    request.system = system.to_owned();
    request.tools = tools.to_vec();
    estimate_context_tokens(&request)
}

/// Splits a conversation into rounds. A round starts at a user message and
/// holds every assistant and tool message that answers it, so a tool call
/// and its result always stay in the same round.
fn split_rounds(conversation: &[ChatMessage]) -> Vec<Vec<ChatMessage>> {
    let mut rounds: Vec<Vec<ChatMessage>> = Vec::new();
    let mut pending: Vec<String> = Vec::new();
    for message in conversation {
        // A tool call and its result must not end up in different rounds, so
        // a round only closes while no tool call is unanswered.
        let starts_round = message.role == Role::User && pending.is_empty();
        if starts_round {
            rounds.push(Vec::new());
        }
        if rounds.is_empty() {
            rounds.push(Vec::new());
        }
        if message.role == Role::Assistant {
            for call in &message.tool_calls {
                pending.push(call.id.clone());
            }
        }
        if message.role == Role::Tool {
            if let Some(id) = message.tool_call_id.as_deref() {
                pending.retain(|open| open != id);
            }
        }
        rounds
            .last_mut()
            .expect("round exists")
            .push(message.clone());
    }
    rounds
}

/// Renders rounds as plain text for the summary request.
fn render_rounds(rounds: &[Vec<ChatMessage>]) -> String {
    let mut text = String::new();
    for round in rounds {
        for message in round {
            let role = match message.role {
                Role::System => "system",
                Role::User => "user",
                Role::Assistant => "assistant",
                Role::Tool => "tool",
            };
            text.push_str(role);
            text.push_str(": ");
            text.push_str(&message.text());
            if !message.tool_calls.is_empty() {
                let names: Vec<&str> = message
                    .tool_calls
                    .iter()
                    .map(|call| call.name.as_str())
                    .collect();
                text.push_str(&format!(" [tools: {}]", names.join(", ")));
            }
            text.push('\n');
        }
        text.push('\n');
    }
    text
}

fn prune_tool_results(conversation: &mut [ChatMessage]) {
    for message in conversation {
        if message.role != Role::Tool {
            continue;
        }
        let length = message.text().chars().count();
        if length <= TOOL_PRUNE_THRESHOLD {
            continue;
        }
        message.content = vec![ContentPart::Text(format!(
            "{TOOL_PRUNE_MARKER_PREFIX}{length}{TOOL_PRUNE_MARKER_SUFFIX}"
        ))];
    }
}

fn compaction_marker(omitted: usize) -> ChatMessage {
    ChatMessage::user(format!(
        "{COMPACTION_MARKER_PREFIX}{omitted}{COMPACTION_MARKER_SUFFIX}"
    ))
}

fn compaction_omitted(message: &ChatMessage) -> Option<usize> {
    let text = message.text();
    let count = text
        .strip_prefix(COMPACTION_MARKER_PREFIX)?
        .strip_suffix(COMPACTION_MARKER_SUFFIX)?;
    count.parse().ok()
}

fn usage_cost_usd(metadata: &ModelMetadata, usage: &Usage) -> Option<f64> {
    let catalog = metadata.catalog?;
    let input = usage.input_tokens as f64 * catalog.input_price_per_million;
    let output = usage.output_tokens as f64 * catalog.output_price_per_million;
    Some((input + output) / 1_000_000.0)
}

fn estimate_tokens(text: &str) -> u64 {
    (text.chars().count() as u64).div_ceil(3)
}

fn estimate_request_tokens(request: &ChatRequest) -> u64 {
    let mut total = estimate_tokens(&request.system);
    for message in &request.messages {
        total += estimate_tokens(&message.text()) + 4;
    }
    if let Ok(tools) = serde_json::to_string(&request.tools) {
        total += estimate_tokens(&tools);
    }
    total
}

fn estimate_context_text_tokens(text: &str) -> u64 {
    (text.len() as u64).div_ceil(3)
}

fn estimate_message_tokens(message: &ChatMessage) -> u64 {
    let serialized = serde_json::to_string(message).unwrap_or_else(|_| message.text());
    estimate_context_text_tokens(&serialized).saturating_add(MESSAGE_OVERHEAD_TOKENS)
}

fn estimate_context_tokens(request: &ChatRequest) -> u64 {
    let mut total =
        estimate_context_text_tokens(&request.system).saturating_add(MESSAGE_OVERHEAD_TOKENS);
    for message in &request.messages {
        total = total.saturating_add(estimate_message_tokens(message));
    }
    if !request.tools.is_empty() {
        if let Ok(tools) = serde_json::to_string(&request.tools) {
            total = total
                .saturating_add(estimate_context_text_tokens(&tools))
                .saturating_add(MESSAGE_OVERHEAD_TOKENS);
        }
    }
    total
}

fn estimate_outcome_tokens(outcome: &ChatOutcome) -> u64 {
    let mut total = estimate_tokens(&outcome.text) + 4;
    for call in &outcome.tool_calls {
        total += estimate_tokens(&call.name) + estimate_tokens(&call.arguments) + 4;
    }
    total
}

fn truncate(text: &str, limit: usize) -> String {
    if text.len() <= limit {
        return text.to_owned();
    }
    let mut end = limit;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}... [truncated]", &text[..end])
}

fn truncate_chars_keep_ends(text: &str, limit: usize) -> String {
    let chars: Vec<char> = text.chars().collect();
    if chars.len() <= limit {
        return text.to_owned();
    }

    const MARKER: &str = "\n... [truncated] ...\n";
    let marker_len = MARKER.chars().count();
    if limit <= marker_len {
        return MARKER.chars().take(limit).collect();
    }

    let keep = limit - marker_len;
    let head_len = keep / 2;
    let tail_len = keep - head_len;
    let head: String = chars[..head_len].iter().collect();
    let tail: String = chars[chars.len() - tail_len..].iter().collect();
    format!("{head}{MARKER}{tail}")
}

#[cfg(test)]
mod tests;

#[cfg(test)]
mod violation_tests;
