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

                    let request = self.prepare_request(history)?;

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
        request
    }

    fn prepare_request(&self, history: &mut Vec<ChatMessage>) -> Result<ChatRequest, AgentError> {
        let request = self.request_for(history);
        let budget = effective_context_budget(&self.metadata);
        let estimated = estimate_context_tokens(&request);
        if estimated <= budget {
            return Ok(request);
        }

        let compacted = compact_history(history, &self.system_prompt, &request.tools, budget)
            .ok_or_else(|| {
                AgentError::ContextWindowExceeded(format!(
                    "estimated {estimated} input tokens exceed budget {budget}"
                ))
            })?;
        let request = self.request_for(&compacted);
        let compacted_estimated = estimate_context_tokens(&request);
        if compacted_estimated >= budget {
            return Err(AgentError::ContextWindowExceeded(format!(
                "compacted request uses {compacted_estimated} input tokens, budget is {budget}"
            )));
        }
        *history = compacted;
        Ok(request)
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
        const MAX_WORKERS: usize = 4;
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

        if evaluation.effect == Effect::Ask {
            let approval_id = format!("{turn_id}:{}", call.id);
            let decision = approvals.request(ApprovalRequest {
                approval_id,
                turn_id: turn_id.to_owned(),
                call_id: call.id.clone(),
                tool: call.name.clone(),
                action: request.action.as_str().to_owned(),
                resource: request.resource.clone(),
                args: args.clone(),
                reason: evaluation.reason.clone(),
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

fn system_prompt(workspace: &std::path::Path) -> String {
    let mut prompt = format!(
        "You are KL, a terminal coding agent. Work only inside the workspace at {}.\n\
         Inspect before editing. Use read_file, grep and glob to gather evidence; use edit_file \
         for exact replacements and write_file for new files. Use shell for builds/tests and git \
         for repository operations. Keep changes minimal, match existing style, and verify with \
         tests when practical. Never claim success without evidence.",
        workspace.display()
    );
    let agents_path = workspace.join("AGENTS.md");
    let Ok(metadata) = std::fs::symlink_metadata(&agents_path) else {
        return prompt;
    };
    if !metadata.file_type().is_file() {
        return prompt;
    }
    let Ok(mut content) = std::fs::read_to_string(&agents_path) else {
        return prompt;
    };
    if content.len() > AGENTS_MD_LIMIT {
        let mut end = AGENTS_MD_LIMIT;
        while !content.is_char_boundary(end) {
            end -= 1;
        }
        content.truncate(end);
        content.push_str("\n[AGENTS.md truncated]");
    }
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
mod tests {
    use super::*;
    use kl_audit::MemoryAudit;
    use kl_policy::{Action, PolicyConfig};
    use kl_provider::{FinishReason, ModelInfo, Protocol, ScriptedProvider, Usage};
    use kl_tools::{Tool, ToolOutcome};
    use std::sync::Mutex;
    use std::sync::atomic::AtomicUsize;
    use std::time::Instant;

    #[derive(Default)]
    struct RecordingSink {
        events: Mutex<Vec<CoreEvent>>,
    }

    impl EventSink for RecordingSink {
        fn emit(&self, event: CoreEvent) -> Result<(), AgentError> {
            self.events.lock().unwrap().push(event);
            Ok(())
        }
    }

    struct SlowProvider;

    impl Provider for SlowProvider {
        fn id(&self) -> &str {
            "slow"
        }

        fn kind(&self) -> &str {
            "slow"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("slow")]
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            cancel: &CancellationToken,
            on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            for index in 0..100 {
                if cancel.is_cancelled() {
                    return Err(ProviderError::cancelled());
                }
                on_event(ChatEvent::TextDelta(format!("chunk-{index}")));
                std::thread::sleep(Duration::from_millis(25));
            }
            Ok(ChatOutcome {
                text: "done".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    struct UsageProvider;

    impl Provider for UsageProvider {
        fn id(&self) -> &str {
            "usage"
        }
        fn kind(&self) -> &str {
            "scripted"
        }
        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("usage")]
        }
        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            on_event(ChatEvent::TextDelta("priced".to_owned()));
            Ok(ChatOutcome {
                text: "priced".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage {
                    input_tokens: 1_000_000,
                    output_tokens: 1_000_000,
                },
            })
        }
    }

    fn metadata() -> ModelMetadata {
        ModelMetadata::new(
            "test",
            "test-model",
            Protocol::Scripted,
            "offline://test",
            32_000,
            0.75,
            1234,
        )
    }

    fn metadata_with_limit(
        context_window: u32,
        compact_threshold: f32,
        max_output_tokens: u32,
    ) -> ModelMetadata {
        ModelMetadata::new(
            "test",
            "test-model",
            Protocol::Scripted,
            "offline://test",
            context_window,
            compact_threshold,
            max_output_tokens,
        )
    }

    #[test]
    fn system_prompt_includes_agents_md() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("AGENTS.md"), "Use tabs.\n").unwrap();

        let prompt = system_prompt(temp.path());

        assert!(prompt.contains("Project instructions from AGENTS.md:\nUse tabs.\n"));
    }

    #[test]
    fn system_prompt_ignores_missing_agents_md() {
        let temp = tempfile::tempdir().unwrap();

        let prompt = system_prompt(temp.path());

        assert!(!prompt.contains("Project instructions from AGENTS.md"));
    }

    #[test]
    fn system_prompt_truncates_agents_md_at_char_boundary() {
        let temp = tempfile::tempdir().unwrap();
        let content = format!("{}é", "a".repeat(AGENTS_MD_LIMIT - 1));
        std::fs::write(temp.path().join("AGENTS.md"), content).unwrap();

        let prompt = system_prompt(temp.path());

        let expected = format!(
            "{}\n[AGENTS.md truncated]\n",
            "a".repeat(AGENTS_MD_LIMIT - 1)
        );
        assert!(prompt.ends_with(&expected));
    }

    fn test_agent(provider: Arc<dyn Provider>, metadata: ModelMetadata) -> Agent {
        let tools = ToolRegistry::builtin();
        for name in tools.names() {
            tools.remove(&name);
        }
        let policy = Arc::new(
            PolicyEngine::new(std::env::current_dir().unwrap(), PolicyConfig::default()).unwrap(),
        );
        Agent::new(
            provider,
            metadata,
            Arc::new(tools),
            policy,
            Arc::new(SandboxManager::policy_only()),
            Arc::new(MemoryAudit::default()),
            AgentConfig::default(),
        )
    }

    struct ValidatingTool {
        executions: Arc<AtomicUsize>,
    }

    impl Tool for ValidatingTool {
        fn name(&self) -> &str {
            "validating"
        }

        fn description(&self) -> &str {
            "validation test tool"
        }

        fn parameters(&self) -> Value {
            serde_json::json!({"type": "object"})
        }

        fn validate(&self, _args: &Value) -> Result<(), ToolError> {
            Err(ToolError::InvalidArgs(
                "required argument missing".to_owned(),
            ))
        }

        fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
            Ok(PolicyRequest {
                action: Action::Read,
                resource: "test".to_owned(),
            })
        }

        fn execute(
            &self,
            _context: &ToolContext<'_>,
            _args: &Value,
        ) -> Result<ToolOutcome, ToolError> {
            self.executions.fetch_add(1, Ordering::Relaxed);
            Ok(ToolOutcome::ok("executed", "executed"))
        }
    }

    struct OutputTool;

    impl Tool for OutputTool {
        fn name(&self) -> &str {
            "output"
        }

        fn description(&self) -> &str {
            "output test tool"
        }

        fn parameters(&self) -> Value {
            serde_json::json!({"type": "object"})
        }

        fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
            Ok(PolicyRequest {
                action: Action::Read,
                resource: "test".to_owned(),
            })
        }

        fn execute(
            &self,
            _context: &ToolContext<'_>,
            _args: &Value,
        ) -> Result<ToolOutcome, ToolError> {
            Ok(ToolOutcome::ok(
                "output",
                format!("HEAD{}TAIL", "x".repeat(200)),
            ))
        }

        fn max_result_chars(&self) -> usize {
            40
        }
    }

    fn test_agent_with_tool(tool: Arc<dyn Tool>, config: AgentConfig) -> (Agent, Arc<MemoryAudit>) {
        let tools = ToolRegistry::builtin();
        for name in tools.names() {
            tools.remove(&name);
        }
        tools.register(tool);
        let policy = Arc::new(
            PolicyEngine::new(std::env::current_dir().unwrap(), PolicyConfig::default()).unwrap(),
        );
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            Arc::new(FailingProvider),
            metadata(),
            Arc::new(tools),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            config,
        );
        (agent, audit)
    }

    #[test]
    fn validation_failure_skips_execution_and_records_invalid_audit() {
        let executions = Arc::new(AtomicUsize::new(0));
        let (agent, audit) = test_agent_with_tool(
            Arc::new(ValidatingTool {
                executions: executions.clone(),
            }),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let call = ToolCall {
            id: "call".to_owned(),
            name: "validating".to_owned(),
            arguments: "{}".to_owned(),
        };

        let payload = agent.execute_tool_call(
            "session",
            "turn",
            &call,
            &AtomicBool::new(false),
            &sink,
            &Gate(ApprovalDecision::Allow),
        );

        let payload: Value = serde_json::from_str(&payload).unwrap();
        assert_eq!(payload["ok"], false);
        assert!(
            payload["error"]
                .as_str()
                .unwrap()
                .contains("required argument missing")
        );
        assert_eq!(executions.load(Ordering::Relaxed), 0);
        let records = audit.records();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].decision, "invalid");
        assert!(!records[0].ok);
        assert!(
            sink.events
                .lock()
                .unwrap()
                .iter()
                .any(|event| matches!(event, CoreEvent::ToolCompleted { ok: false, .. }))
        );
    }

    #[test]
    fn result_chars_limit_keeps_head_and_tail() {
        let (agent, audit) = test_agent_with_tool(
            Arc::new(OutputTool),
            AgentConfig {
                max_tool_output_bytes: 4096,
                ..AgentConfig::default()
            },
        );
        let sink = RecordingSink::default();
        let call = ToolCall {
            id: "call".to_owned(),
            name: "output".to_owned(),
            arguments: "{}".to_owned(),
        };

        let payload = agent.execute_tool_call(
            "session",
            "turn",
            &call,
            &AtomicBool::new(false),
            &sink,
            &Gate(ApprovalDecision::Allow),
        );
        let payload: Value = serde_json::from_str(&payload).unwrap();
        let output = payload["output"].as_str().unwrap();
        assert_eq!(output.chars().count(), 40);
        assert!(output.starts_with("HEAD"));
        assert!(output.ends_with("TAIL"));
        assert!(output.contains("... [truncated] ..."));

        let events = sink.events.lock().unwrap();
        let emitted: String = events
            .iter()
            .filter_map(|event| match event {
                CoreEvent::ToolOutput { chunk, .. } => Some(chunk.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(emitted, output);
        let records = audit.records();
        assert_eq!(records.len(), 1);
        assert!(records[0].ok);
    }

    #[derive(Default)]
    struct CapturingProvider {
        requests: Mutex<Vec<ChatRequest>>,
    }

    impl Provider for CapturingProvider {
        fn id(&self) -> &str {
            "capturing"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            Vec::new()
        }

        fn stream_chat(
            &self,
            request: &ChatRequest,
            _cancel: &CancellationToken,
            on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            self.requests.lock().unwrap().push(request.clone());
            on_event(ChatEvent::TextDelta("done".to_owned()));
            Ok(ChatOutcome {
                text: "done".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    struct Gate(ApprovalDecision);

    impl ApprovalGate for Gate {
        fn request(&self, _request: ApprovalRequest) -> ApprovalDecision {
            self.0
        }
    }

    struct FailingProvider;

    impl Provider for FailingProvider {
        fn id(&self) -> &str {
            "failing"
        }

        fn kind(&self) -> &str {
            "openai-chat"
        }

        fn models(&self) -> Vec<ModelInfo> {
            vec![ModelInfo::new("failing")]
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            Err(ProviderError::new(
                ProviderErrorKind::Server,
                "provider boom",
            ))
        }
    }
    struct MetadataProvider;

    impl Provider for MetadataProvider {
        fn id(&self) -> &str {
            "metadata"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            Vec::new()
        }

        fn stream_chat(
            &self,
            request: &ChatRequest,
            _cancel: &CancellationToken,
            on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            assert_eq!(request.model, "test-model");
            assert_eq!(request.max_tokens, 1234);
            on_event(ChatEvent::TextDelta("ok".to_owned()));
            Ok(ChatOutcome {
                text: "ok".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    #[test]
    fn exposes_model_metadata_and_uses_max_output_tokens() {
        let temp = tempfile::tempdir().unwrap();
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let agent = Agent::new(
            Arc::new(MetadataProvider),
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            Arc::new(MemoryAudit::default()),
            AgentConfig::default(),
        );
        assert_eq!(agent.metadata().context_window, 32_000);
        assert_eq!(agent.metadata().compact_threshold, 0.75);
        assert_eq!(agent.metadata().max_output_tokens, 1234);
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let result = agent
            .run_turn(
                "session",
                "turn",
                "go",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();
        assert_eq!(result.status, TurnStatus::Completed);
    }

    #[test]
    fn context_budget_uses_threshold_and_output_reserve_without_zero() {
        assert_eq!(
            effective_context_budget(&metadata_with_limit(1_000, 0.8, 100)),
            800
        );
        assert_eq!(
            effective_context_budget(&metadata_with_limit(1_000, 0.9, 400)),
            600
        );
        assert_eq!(
            effective_context_budget(&metadata_with_limit(1_000, 0.8, 2_000)),
            1
        );
        assert_eq!(
            effective_context_budget(&metadata_with_limit(1_000, 0.0, 100)),
            1
        );
        assert_eq!(
            effective_context_budget(&metadata_with_limit(1, f32::NAN, 0)),
            1
        );
    }

    #[test]
    fn token_estimate_includes_tool_calls_and_tool_results() {
        let short = estimate_message_tokens(&ChatMessage::user("short"));
        let long_user = estimate_message_tokens(&ChatMessage::user("u".repeat(600)));
        let tool_call = estimate_message_tokens(&ChatMessage::assistant_with_text(
            "",
            vec![ToolCall {
                id: "call-1".to_owned(),
                name: "read_file".to_owned(),
                arguments: format!("{{\"path\":\"{}\"}}", "a".repeat(600)),
            }],
        ));
        let tool_result = estimate_message_tokens(&ChatMessage::tool("call-1", "r".repeat(600)));

        assert!(long_user > short);
        assert!(tool_call > short);
        assert!(tool_result > short);
    }

    #[test]
    fn turn_completed_event_carries_usage_and_cost() {
        let metadata = ModelMetadata::new(
            "openai",
            "gpt-5",
            Protocol::Scripted,
            "offline://test",
            400_000,
            0.8,
            128_000,
        )
        .with_catalog(kl_provider::lookup_model("openai", "gpt-5"));
        let agent = test_agent(Arc::new(UsageProvider), metadata);
        let sink = RecordingSink::default();
        run_once(&agent, &sink).unwrap();
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|event| matches!(
            event,
            CoreEvent::TurnCompleted {
                input_tokens: 1_000_000,
                output_tokens: 1_000_000,
                cost_usd: Some(cost),
                ..
            } if (*cost - 11.25).abs() < 1e-9
        )));
    }

    #[test]
    fn catalog_price_computes_turn_cost() {
        let metadata = ModelMetadata::new(
            "openai",
            "gpt-5",
            kl_provider::Protocol::OpenAiResponses,
            "https://example.invalid/responses",
            400_000,
            0.8,
            128_000,
        )
        .with_catalog(kl_provider::lookup_model("openai", "gpt-5"));
        let usage = Usage {
            input_tokens: 1_000_000,
            output_tokens: 1_000_000,
        };
        assert_eq!(usage_cost_usd(&metadata, &usage), Some(11.25));
    }

    #[test]
    fn compaction_preserves_system_latest_user_and_complete_tool_pairs() {
        let old_call = ToolCall {
            id: "old-call".to_owned(),
            name: "read_file".to_owned(),
            arguments: "{\"path\":\"old.txt\"}".to_owned(),
        };
        let current_call = ToolCall {
            id: "current-call".to_owned(),
            name: "read_file".to_owned(),
            arguments: "{\"path\":\"current.txt\"}".to_owned(),
        };
        let system = ChatMessage::system("persistent system");
        let latest_user = ChatMessage::user("latest request");
        let current_assistant = ChatMessage::assistant_with_text("", vec![current_call]);
        let current_tool = ChatMessage::tool("current-call", "current result");
        let history = vec![
            system.clone(),
            ChatMessage::user("o".repeat(800)),
            ChatMessage::assistant_with_text("", vec![old_call]),
            ChatMessage::tool("old-call", "old result"),
            ChatMessage::assistant_with_text("old answer", Vec::new()),
            latest_user.clone(),
            current_assistant.clone(),
            current_tool.clone(),
        ];
        let minimum = vec![
            system.clone(),
            compaction_marker(4),
            latest_user.clone(),
            current_assistant.clone(),
            current_tool.clone(),
        ];
        let budget = estimate_history_tokens("", &[], &minimum).saturating_add(1);

        let compacted = compact_history(&history, "", &[], budget).unwrap();

        assert_eq!(compacted[0], system);
        assert_eq!(compaction_omitted(&compacted[1]), Some(4));
        assert_eq!(compacted[2], latest_user);
        assert!(
            compacted
                .iter()
                .all(|message| !message.text().contains(&"o".repeat(100)))
        );
        assert!(estimate_history_tokens("", &[], &compacted) < budget);

        let mut known_calls = Vec::new();
        for message in &compacted {
            if message.role == Role::Assistant {
                known_calls.extend(message.tool_calls.iter().map(|call| call.id.as_str()));
            }
            if message.role == Role::Tool {
                let call_id = message.tool_call_id.as_deref().unwrap();
                assert!(known_calls.contains(&call_id));
            }
        }
        assert_eq!(
            compacted
                .iter()
                .filter(|message| message.role == Role::Tool)
                .count(),
            1
        );
    }

    #[test]
    fn large_tool_results_are_pruned_before_dropping_rounds() {
        let call = ToolCall {
            id: "old-call".to_owned(),
            name: "read_file".to_owned(),
            arguments: "{\"path\":\"old.txt\"}".to_owned(),
        };
        let history = vec![
            ChatMessage::system("persistent system"),
            ChatMessage::user("old request"),
            ChatMessage::assistant_with_text("", vec![call]),
            ChatMessage::tool("old-call", "x".repeat(5000)),
            ChatMessage::assistant_with_text("old answer", Vec::new()),
            ChatMessage::user("latest request"),
        ];
        let mut expected = history.clone();
        expected[3].content = vec![ContentPart::Text(format!(
            "{TOOL_PRUNE_MARKER_PREFIX}5000{TOOL_PRUNE_MARKER_SUFFIX}"
        ))];
        let budget = estimate_history_tokens("", &[], &expected).saturating_add(1);

        let compacted = compact_history(&history, "", &[], budget).unwrap();

        assert_eq!(compacted, expected);
        assert!(
            compacted
                .iter()
                .all(|message| compaction_omitted(message).is_none())
        );
        assert!(
            compacted
                .iter()
                .any(|message| message.text().contains(TOOL_PRUNE_MARKER_PREFIX))
        );
    }

    #[test]
    fn history_is_unchanged_when_request_is_within_budget() {
        let provider = Arc::new(CapturingProvider::default());
        let agent = test_agent(provider, metadata_with_limit(32_000, 0.75, 1_234));
        let mut history = vec![
            ChatMessage::system("persistent system"),
            ChatMessage::user("old user"),
            ChatMessage::assistant_with_text("old answer", Vec::new()),
            ChatMessage::user("latest request"),
        ];
        let original = history.clone();

        let request = agent.prepare_request(&mut history).unwrap();

        assert_eq!(history, original);
        assert_eq!(request.messages, original);
        assert!(
            history
                .iter()
                .all(|message| compaction_omitted(message).is_none())
        );
    }

    #[test]
    fn compacts_before_sending_provider_request() {
        let provider = Arc::new(CapturingProvider::default());
        let agent = test_agent(provider.clone(), metadata_with_limit(900, 0.5, 100));
        let mut history = vec![ChatMessage::system("persistent system")];
        for index in 0..8 {
            history.push(ChatMessage::user(format!(
                "old user {index} {}",
                "u".repeat(200)
            )));
            history.push(ChatMessage::assistant_with_text(
                format!("old answer {index} {}", "a".repeat(200)),
                Vec::new(),
            ));
        }
        let original_len = history.len();
        let sink = RecordingSink::default();

        agent
            .run_turn(
                "session",
                "turn",
                "latest request",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();

        let requests = provider.requests.lock().unwrap();
        let request = &requests[0];
        assert_eq!(request.messages[0].text(), "persistent system");
        assert_eq!(request.messages.last().unwrap().text(), "latest request");
        assert!(
            request
                .messages
                .iter()
                .any(|message| { compaction_omitted(message).is_some_and(|count| count > 0) })
        );
        assert!(
            estimate_context_tokens(request)
                < effective_context_budget(&metadata_with_limit(900, 0.5, 100))
        );
        assert!(history.len() < original_len);
    }

    #[test]
    fn repeated_turns_keep_history_bounded() {
        let provider = Arc::new(CapturingProvider::default());
        let agent = test_agent(provider, metadata_with_limit(1_000, 0.6, 100));
        let mut history = vec![ChatMessage::system("persistent system")];

        for index in 0..40 {
            history.push(ChatMessage::user(format!(
                "turn {index} {}",
                "u".repeat(160)
            )));
            let request = agent.prepare_request(&mut history).unwrap();
            assert!(
                estimate_context_tokens(&request)
                    <= effective_context_budget(&metadata_with_limit(1_000, 0.6, 100))
            );
            history.push(ChatMessage::assistant_with_text(
                format!("answer {index} {}", "a".repeat(160)),
                Vec::new(),
            ));
        }

        assert!(
            history.len() <= 10,
            "history grew to {} messages",
            history.len()
        );
    }

    #[test]
    fn tiny_context_returns_error_instead_of_panicking() {
        let provider = Arc::new(CapturingProvider::default());
        let agent = test_agent(provider, metadata_with_limit(1, 0.5, 100));
        let mut history = vec![ChatMessage::user("small")];

        let error = agent.prepare_request(&mut history).unwrap_err();

        assert!(matches!(error, AgentError::ContextWindowExceeded(_)));
    }

    #[test]
    fn cancel_interrupts_slow_provider_promptly() {
        let temp = tempfile::tempdir().unwrap();
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let agent = Agent::new(
            Arc::new(SlowProvider),
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            Arc::new(MemoryAudit::default()),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let cancel = Arc::new(AtomicBool::new(false));
        let cancel_for_thread = cancel.clone();
        let handle = std::thread::spawn(move || {
            let mut history = Vec::new();
            agent.run_turn(
                "session",
                "turn",
                "go",
                &mut history,
                cancel_for_thread,
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
        });

        std::thread::sleep(Duration::from_millis(200));
        let started = Instant::now();
        cancel.store(true, Ordering::Relaxed);
        let result = handle.join().unwrap().unwrap();
        assert_eq!(result.status, TurnStatus::Cancelled);
        assert!(
            started.elapsed() < Duration::from_secs(1),
            "cancellation took {:?}",
            started.elapsed()
        );
    }

    #[test]
    fn runs_scripted_read_edit_turn() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "old value\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[
                    {"tool_calls":[{"name":"read_file","arguments":{"path":"a.txt"}}]},
                    {"tool_calls":[{"name":"edit_file","arguments":{"path":"a.txt","old_string":"old","new_string":"new"}}]},
                    {"text":"done"}
                ]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(
            PolicyEngine::new(
                temp.path(),
                PolicyConfig {
                    auto_approve_edits: true,
                    ..PolicyConfig::default()
                },
            )
            .unwrap(),
        );
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let result = agent
            .run_turn(
                "session",
                "turn",
                "fix it",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();

        assert_eq!(result.status, TurnStatus::Completed);
        assert_eq!(result.text, "done");
        assert_eq!(
            std::fs::read_to_string(temp.path().join("a.txt")).unwrap(),
            "new value\n"
        );
        assert_eq!(audit.records().len(), 2);
        let events = sink.events.lock().unwrap();
        assert!(
            events
                .iter()
                .any(|event| matches!(event, CoreEvent::TurnDelta { .. }))
        );
        assert!(
            events
                .iter()
                .any(|event| matches!(event, CoreEvent::ToolCompleted { ok: true, .. }))
        );
        assert!(
            events
                .iter()
                .any(|event| matches!(event, CoreEvent::TurnCompleted { .. }))
        );
    }

    #[test]
    fn no_usage_provider_uses_conservative_token_budget() {
        let temp = tempfile::tempdir().unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"text":"this response has no usage and must still be budgeted"}]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            Arc::new(MemoryAudit::default()),
            AgentConfig {
                max_total_tokens: 1,
                ..AgentConfig::default()
            },
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let error = agent
            .run_turn(
                "session",
                "turn",
                "budget this turn",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap_err();
        assert!(matches!(error, AgentError::TokenBudgetExceeded));
    }

    #[test]
    fn read_only_tool_access_denies_edit_before_execution() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "old value\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[
                    {"tool_calls":[{"name":"edit_file","arguments":{"path":"a.txt","old_string":"old","new_string":"new"}}]},
                    {"text":"done"}
                ]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(
            PolicyEngine::new(
                temp.path(),
                PolicyConfig {
                    auto_approve_edits: true,
                    ..PolicyConfig::default()
                },
            )
            .unwrap(),
        );
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig {
                tool_access: ToolAccess::ReadOnly,
                ..AgentConfig::default()
            },
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let result = agent
            .run_turn(
                "session",
                "turn",
                "do not edit",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();
        assert_eq!(result.status, TurnStatus::Completed);
        assert_eq!(
            std::fs::read_to_string(temp.path().join("a.txt")).unwrap(),
            "old value\n"
        );
        let records = audit.records();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].decision, "deny");
        assert!(
            records[0]
                .error
                .as_deref()
                .is_some_and(|error| error.contains("review-only"))
        );
    }

    #[test]
    fn stops_at_max_iterations_with_explicit_failure() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "contents\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[
                    {"tool_calls":[{"name":"read_file","arguments":{"path":"a.txt"}}]},
                    {"tool_calls":[{"name":"read_file","arguments":{"path":"a.txt"}}]}
                ]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig {
                max_iterations: 2,
                ..AgentConfig::default()
            },
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let error = agent
            .run_turn(
                "session",
                "turn",
                "keep looping",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap_err();

        assert!(matches!(error, AgentError::MaxIterations));
        assert_eq!(audit.records().len(), 2);
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|event| {
            matches!(event, CoreEvent::TurnFailed { message, .. }
                if message.contains("maximum") || message.contains("iterations"))
        }));
    }
    #[test]
    fn every_tool_call_has_exactly_one_audit_record() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "contents\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[
                    {"tool_calls":[
                        {"id":"call-read","name":"read_file","arguments":{"path":"a.txt"}},
                        {"id":"call-deny","name":"shell","arguments":{"command":"rm -rf /"}},
                        {"id":"call-missing","name":"read_file","arguments":{"path":"missing.txt"}},
                        {"id":"call-unknown","name":"unknown_tool","arguments":{}}
                    ]},
                    {"text":"done"}
                ]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let result = agent
            .run_turn(
                "session",
                "turn",
                "exercise audits",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();

        assert_eq!(result.text, "done");
        let records = audit.records();
        assert_eq!(records.len(), 4, "{records:#?}");
        assert_eq!(
            records
                .iter()
                .map(|record| record.call_id.as_str())
                .collect::<std::collections::HashSet<_>>()
                .len(),
            4
        );
        assert_eq!(
            records
                .iter()
                .find(|record| record.call_id == "call-read")
                .unwrap()
                .decision,
            "allow"
        );
        assert_eq!(
            records
                .iter()
                .find(|record| record.call_id == "call-deny")
                .unwrap()
                .decision,
            "deny"
        );
        assert!(records.iter().all(|record| !record.args_hash.is_empty()));
        assert!(records.iter().all(|record| !record.output_hash.is_empty()));
        assert!(
            records
                .iter()
                .find(|record| record.call_id == "call-read")
                .unwrap()
                .ok
        );
        assert!(
            records
                .iter()
                .filter(|record| record.call_id != "call-read")
                .all(|record| !record.ok)
        );
    }
    #[test]
    fn provider_failure_emits_turn_failed() {
        let temp = tempfile::tempdir().unwrap();
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let agent = Agent::new(
            Arc::new(FailingProvider),
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            Arc::new(MemoryAudit::default()),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let error = agent
            .run_turn(
                "session",
                "turn",
                "hello",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap_err();

        assert!(matches!(error, AgentError::Provider(_)));
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|event| {
            matches!(event, CoreEvent::TurnFailed { message, .. }
                if message.contains("provider boom"))
        }));
    }

    /// Agent whose provider retries back off almost immediately.
    fn retrying_agent(provider: Arc<dyn Provider>, base_delay: Duration) -> Agent {
        let mut agent = test_agent(provider, metadata());
        agent.retry = RetryPolicy {
            max_attempts: 3,
            base_delay,
        };
        agent
    }

    fn run_once(agent: &Agent, sink: &RecordingSink) -> Result<TurnResult, AgentError> {
        let mut history = Vec::new();
        agent.run_turn(
            "session",
            "turn",
            "hello",
            &mut history,
            Arc::new(AtomicBool::new(false)),
            sink,
            &Gate(ApprovalDecision::Allow),
        )
    }

    #[test]
    fn retries_retryable_provider_error_until_success() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"rate_limit","message":"slow down"}},
                    {"error":{"kind":"server","message":"boom"}},
                    {"text":"recovered"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        let result = run_once(&agent, &sink).unwrap();

        assert_eq!(result.text, "recovered");
        assert_eq!(scripted.call_count(), 3);
        let events = sink.events.lock().unwrap();
        let deltas: Vec<&str> = events
            .iter()
            .filter_map(|event| match event {
                CoreEvent::TurnDelta { text, .. } => Some(text.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(deltas, vec!["recovered"]);
    }

    #[test]
    fn retries_timeout_provider_error() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"timeout","message":"provider timeout"}},
                    {"text":"recovered"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        assert_eq!(run_once(&agent, &sink).unwrap().text, "recovered");
        assert_eq!(scripted.call_count(), 2);
    }

    #[test]
    fn retries_stream_disconnect_before_delta() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"transport","message":"stream disconnected"}},
                    {"text":"recovered"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        assert_eq!(run_once(&agent, &sink).unwrap().text, "recovered");
        assert_eq!(scripted.call_count(), 2);
    }

    #[test]
    fn does_not_retry_non_retryable_provider_error() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"invalid_response","message":"bad payload"}},
                    {"text":"never reached"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        let error = run_once(&agent, &sink).unwrap_err();

        assert!(matches!(error, AgentError::Provider(_)));
        assert_eq!(scripted.call_count(), 1);
    }

    #[test]
    fn does_not_retry_cancelled_provider_error() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"cancelled","message":"stopped"}},
                    {"text":"never reached"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        let result = run_once(&agent, &sink).unwrap();

        assert_eq!(result.status, TurnStatus::Cancelled);
        assert_eq!(scripted.call_count(), 1);
    }

    #[test]
    fn does_not_retry_after_a_delta_was_streamed() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"text":"partial","error":{"kind":"server","message":"boom"}},
                    {"text":"never reached"}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_millis(1));
        let sink = RecordingSink::default();

        let error = run_once(&agent, &sink).unwrap_err();

        assert!(matches!(error, AgentError::Provider(_)));
        assert_eq!(scripted.call_count(), 1);
    }

    #[test]
    fn cancellation_interrupts_retry_backoff() {
        let scripted = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"error":{"kind":"rate_limit","message":"slow down"}}]"#,
            )
            .unwrap(),
        );
        let provider: Arc<dyn Provider> = scripted.clone();
        let agent = retrying_agent(provider, Duration::from_secs(10));
        let sink = RecordingSink::default();
        let cancel = Arc::new(AtomicBool::new(false));
        let cancel_for_thread = cancel.clone();
        let started = Instant::now();
        let handle = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(50));
            cancel_for_thread.store(true, Ordering::Relaxed);
        });
        let mut history = Vec::new();

        let result = agent
            .run_turn(
                "session",
                "turn",
                "hello",
                &mut history,
                cancel,
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap();

        handle.join().unwrap();
        assert_eq!(result.status, TurnStatus::Cancelled);
        assert!(started.elapsed() < Duration::from_secs(5));
        assert_eq!(scripted.call_count(), 1);
    }

    #[test]
    fn oversized_tool_batch_fails_before_execution() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "contents\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"tool_calls":[
                    {"name":"read_file","arguments":{"path":"a.txt"}},
                    {"name":"read_file","arguments":{"path":"a.txt"}},
                    {"name":"read_file","arguments":{"path":"a.txt"}}
                ]}]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig {
                max_tool_calls_per_turn: 2,
                ..AgentConfig::default()
            },
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        let error = agent
            .run_turn(
                "session",
                "turn",
                "too many tools",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Allow),
            )
            .unwrap_err();

        assert!(matches!(error, AgentError::Tool(_)));
        assert!(audit.records().is_empty());
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|event| {
            matches!(event, CoreEvent::TurnFailed { message, .. }
                if message.contains("tool call limit exceeded"))
        }));
    }
    #[test]
    fn denied_edit_is_not_applied() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("a.txt"), "old value\n").unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"tool_calls":[{"name":"edit_file","arguments":{"path":"a.txt","old_string":"old","new_string":"new"}}]}]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            metadata(),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig::default(),
        );
        let sink = RecordingSink::default();
        let mut history = Vec::new();
        agent
            .run_turn(
                "session",
                "turn",
                "fix it",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate(ApprovalDecision::Deny),
            )
            .unwrap();
        assert_eq!(
            std::fs::read_to_string(temp.path().join("a.txt")).unwrap(),
            "old value\n"
        );
        let records = audit.records();
        assert_eq!(records.len(), 1);
        assert_eq!(records[0].approved, Some(false));
        assert!(!records[0].ok);
    }
    /// Provider that answers the summary request with a fixed text and counts
    /// how many requests it saw.
    struct SummaryProvider {
        text: String,
        requests: Mutex<Vec<ChatRequest>>,
    }

    impl Provider for SummaryProvider {
        fn id(&self) -> &str {
            "summary"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            Vec::new()
        }

        fn stream_chat(
            &self,
            request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            self.requests.lock().unwrap().push(request.clone());
            Ok(ChatOutcome {
                text: self.text.clone(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }

    /// Provider that always fails, so the summary path has to fall back.
    struct BrokenSummaryProvider;

    impl Provider for BrokenSummaryProvider {
        fn id(&self) -> &str {
            "broken-summary"
        }

        fn kind(&self) -> &str {
            "test"
        }

        fn models(&self) -> Vec<ModelInfo> {
            Vec::new()
        }

        fn stream_chat(
            &self,
            _request: &ChatRequest,
            _cancel: &CancellationToken,
            _on_event: &mut dyn FnMut(ChatEvent),
        ) -> Result<ChatOutcome, ProviderError> {
            Err(ProviderError::new(
                ProviderErrorKind::Server,
                "summary boom",
            ))
        }
    }

    /// History with several rounds, large enough to exceed the budget.
    fn oversized_history() -> Vec<ChatMessage> {
        let mut history = vec![ChatMessage::system("persistent system")];
        for index in 0..10 {
            history.push(ChatMessage::user(format!(
                "old user {index} {}",
                "u".repeat(600)
            )));
            history.push(ChatMessage::assistant_with_text(
                format!("old answer {index} {}", "a".repeat(600)),
                Vec::new(),
            ));
        }
        history.push(ChatMessage::user("latest request"));
        history.push(ChatMessage::assistant_with_text(
            "latest answer",
            Vec::new(),
        ));
        history
    }

    #[test]
    fn summary_compaction_replaces_old_rounds_with_a_model_summary() {
        let provider = Arc::new(SummaryProvider {
            text: "The session discussed the old rounds.".to_owned(),
            requests: Mutex::new(Vec::new()),
        });
        let agent = test_agent(provider.clone(), metadata_with_limit(4_000, 0.9, 100));
        let history = oversized_history();

        let result = agent.compact_history(&history).unwrap();

        assert!(result.summary_used);
        assert_eq!(
            result.summary.as_deref(),
            Some("The session discussed the old rounds.")
        );
        assert!(result.omitted_messages > 0);
        let summary = result
            .history
            .iter()
            .find(|message| message.text().starts_with(SUMMARY_HEADER))
            .expect("summary message");
        assert!(
            summary
                .text()
                .contains("The session discussed the old rounds.")
        );
        // The oldest rounds are gone, the newest round is kept verbatim.
        assert!(
            !result
                .history
                .iter()
                .any(|message| message.text().contains("old user 0"))
        );
        assert!(
            result
                .history
                .iter()
                .any(|message| message.text() == "latest request")
        );
        // The summary request is not part of the conversation.
        let requests = provider.requests.lock().unwrap();
        assert_eq!(requests.len(), 1);
        assert_eq!(requests[0].system, SUMMARY_INSTRUCTIONS);
        assert!(requests[0].tools.is_empty());
    }

    #[test]
    fn summary_failure_falls_back_to_the_mechanical_trim() {
        let agent = test_agent(
            Arc::new(BrokenSummaryProvider),
            metadata_with_limit(4_000, 0.9, 100),
        );
        let history = oversized_history();

        let result = agent.compact_history(&history).unwrap();

        assert!(!result.summary_used);
        assert!(result.summary.is_none());
        assert!(result.omitted_messages > 0);
        assert!(
            result
                .history
                .iter()
                .any(|message| { compaction_omitted(message).is_some() })
        );
        assert!(
            result
                .history
                .iter()
                .any(|message| message.text() == "latest request")
        );
    }

    #[test]
    fn summary_compaction_keeps_tool_calls_paired_with_their_results() {
        let provider = Arc::new(SummaryProvider {
            text: "Earlier rounds were summarized.".to_owned(),
            requests: Mutex::new(Vec::new()),
        });
        let agent = test_agent(provider.clone(), metadata_with_limit(4_000, 0.9, 100));
        let mut history = vec![ChatMessage::system("persistent system")];
        for index in 0..10 {
            let call = ToolCall {
                id: format!("old-call-{index}"),
                name: "read_file".to_owned(),
                arguments: "{\"path\":\"a.txt\"}".to_owned(),
            };
            history.push(ChatMessage::user(format!(
                "old user {index} {}",
                "u".repeat(600)
            )));
            history.push(ChatMessage::assistant_with_text(
                format!("old answer {index} {}", "a".repeat(200)),
                vec![call.clone()],
            ));
            history.push(ChatMessage::tool(&call.id, "file contents"));
        }
        history.push(ChatMessage::user("latest request"));
        let latest = ToolCall {
            id: "latest-call".to_owned(),
            name: "read_file".to_owned(),
            arguments: "{\"path\":\"b.txt\"}".to_owned(),
        };
        history.push(ChatMessage::assistant_with_text(
            "latest answer",
            vec![latest.clone()],
        ));
        history.push(ChatMessage::tool(&latest.id, "latest contents"));

        let result = agent.compact_history(&history).unwrap();

        assert!(result.summary_used);
        let mut known = Vec::new();
        for message in &result.history {
            if message.role == Role::Assistant {
                known.extend(message.tool_calls.iter().map(|call| call.id.as_str()));
            }
            if message.role == Role::Tool {
                let id = message.tool_call_id.as_deref().unwrap();
                assert!(known.contains(&id), "tool result without its call");
            }
        }
        assert!(known.contains(&"latest-call"));
        assert!(!known.contains(&"old-call-0"));
    }

    #[test]
    fn summary_compaction_keeps_the_recent_rounds_verbatim() {
        let provider = Arc::new(SummaryProvider {
            text: "Earlier rounds were summarized.".to_owned(),
            requests: Mutex::new(Vec::new()),
        });
        let agent = test_agent(provider.clone(), metadata_with_limit(4_000, 0.9, 100));
        let history = oversized_history();
        let recent: Vec<String> = history
            .iter()
            .rev()
            .take(2)
            .map(|message| message.text())
            .collect();

        let result = agent.compact_history(&history).unwrap();

        assert!(result.summary_used);
        for text in &recent {
            assert!(
                result.history.iter().any(|message| &message.text() == text),
                "recent round missing: {text}"
            );
        }
    }

    #[test]
    fn compaction_within_budget_never_calls_the_provider() {
        let provider = Arc::new(SummaryProvider {
            text: "should not be used".to_owned(),
            requests: Mutex::new(Vec::new()),
        });
        let agent = test_agent(provider.clone(), metadata_with_limit(32_000, 0.75, 1_234));
        let history = vec![
            ChatMessage::system("persistent system"),
            ChatMessage::user("hello"),
            ChatMessage::assistant_with_text("hi", Vec::new()),
        ];

        let result = agent.compact_history(&history).unwrap();

        assert!(!result.summary_used);
        assert!(result.summary.is_none());
        assert_eq!(result.omitted_messages, 0);
        assert!(provider.requests.lock().unwrap().is_empty());
    }
}

#[cfg(test)]
mod violation_tests {
    use super::*;
    use kl_audit::MemoryAudit;
    use kl_policy::PolicyConfig;
    use kl_provider::{Protocol, ScriptedProvider};
    use std::sync::atomic::AtomicBool;
    use std::sync::{Arc, Mutex};

    #[derive(Default)]
    struct Sink {
        events: Mutex<Vec<CoreEvent>>,
    }

    impl EventSink for Sink {
        fn emit(&self, event: CoreEvent) -> Result<(), AgentError> {
            self.events.lock().unwrap().push(event);
            Ok(())
        }
    }

    struct Gate;

    impl ApprovalGate for Gate {
        fn request(&self, _request: ApprovalRequest) -> ApprovalDecision {
            ApprovalDecision::Allow
        }
    }

    #[test]
    fn policy_denial_emits_sandbox_violation_and_one_audit_record() {
        let temp = tempfile::tempdir().unwrap();
        let provider = Arc::new(
            ScriptedProvider::from_json(
                "scripted",
                r#"[{"tool_calls":[{"id":"call-1","name":"read_file","arguments":{"path":"../secret.txt"}}]},{"text":"done"}]"#,
            )
            .unwrap(),
        );
        let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
        let audit = Arc::new(MemoryAudit::default());
        let agent = Agent::new(
            provider,
            ModelMetadata::new(
                "test",
                "test-model",
                Protocol::Scripted,
                "offline://test",
                32_000,
                0.75,
                1234,
            ),
            Arc::new(ToolRegistry::builtin()),
            policy,
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig::default(),
        );
        let sink = Sink::default();
        let mut history = Vec::new();
        agent
            .run_turn(
                "session",
                "turn",
                "go",
                &mut history,
                Arc::new(AtomicBool::new(false)),
                &sink,
                &Gate,
            )
            .unwrap();

        let records = audit.records();
        assert_eq!(records.len(), 1);
        assert!(!records[0].ok);
        assert_eq!(records[0].decision, "deny");
        let events = sink.events.lock().unwrap();
        assert!(events.iter().any(|event| matches!(
            event,
            CoreEvent::SandboxViolation { kind, .. } if kind == "policy_denied"
        )));
    }
}
