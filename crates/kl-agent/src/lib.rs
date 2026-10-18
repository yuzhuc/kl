//! Explicit agent loop. Provider differences never appear here: the loop sees
//! one neutral chat request/stream and one neutral tool call type.

use kl_audit::{AuditRecord, AuditSink};
use kl_policy::{Effect, PolicyEngine};
use kl_protocol::{ApprovalDecision, CoreEvent};
use kl_provider::{
    CancellationToken, ChatEvent, ChatMessage, ChatOutcome, ChatRequest, ModelMetadata, Provider,
    ProviderError, ProviderErrorKind, ToolCall,
};
use kl_sandbox::SandboxManager;
use kl_tools::{PolicyRequest, ToolContext, ToolError, ToolRegistry};
use serde_json::{Value, json};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant};

#[derive(Debug, Clone)]
pub struct AgentConfig {
    pub max_iterations: usize,
    pub max_tool_calls_per_turn: usize,
    pub max_tool_output_bytes: usize,
    pub command_timeout: Duration,
}

impl Default for AgentConfig {
    fn default() -> Self {
        Self {
            max_iterations: 12,
            max_tool_calls_per_turn: 32,
            max_tool_output_bytes: 32 * 1024,
            command_timeout: Duration::from_secs(120),
        }
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
            system_prompt,
        }
    }

    pub fn metadata(&self) -> &ModelMetadata {
        &self.metadata
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
        history.push(ChatMessage::user(prompt));
        sink.emit(CoreEvent::TurnStarted {
            turn_id: turn_id.to_owned(),
        })?;

        let mut state = TurnState::Model { iteration: 0 };
        loop {
            if cancel.load(Ordering::Relaxed) {
                return self.finish_cancelled(turn_id, history, sink);
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

                    let mut request =
                        ChatRequest::new(self.metadata.model.clone(), history.clone());
                    request.max_tokens = self.metadata.max_output_tokens;
                    request.system = self.system_prompt.clone();
                    request.tools = self.tools.definitions();

                    let outcome = match self.stream_provider(request, cancel.clone(), sink, turn_id)
                    {
                        Ok(outcome) => outcome,
                        Err(AgentError::Provider(error))
                            if error.kind == ProviderErrorKind::Cancelled =>
                        {
                            return self.finish_cancelled(turn_id, history, sink);
                        }
                        Err(error) => {
                            let _ = sink.emit(CoreEvent::TurnFailed {
                                turn_id: turn_id.to_owned(),
                                message: error.to_string(),
                            });
                            return Err(error);
                        }
                    };

                    if cancel.load(Ordering::Relaxed) {
                        return self.finish_cancelled(turn_id, history, sink);
                    }

                    if outcome.tool_calls.is_empty() {
                        history.push(ChatMessage::assistant_with_text(
                            outcome.text.clone(),
                            Vec::new(),
                        ));
                        sink.emit(CoreEvent::TurnCompleted {
                            turn_id: turn_id.to_owned(),
                            status: TurnStatus::Completed.as_str().to_owned(),
                            text: outcome.text.clone(),
                        })?;
                        return Ok(TurnResult {
                            status: TurnStatus::Completed,
                            text: outcome.text,
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
                    if index >= calls.len() {
                        TurnState::Model {
                            iteration: iteration + 1,
                        }
                    } else {
                        let call = &calls[index];
                        let result = self
                            .execute_tool_call(session_id, turn_id, call, &cancel, sink, approvals);
                        history.push(ChatMessage::tool(&call.id, result));
                        if cancel.load(Ordering::Relaxed) {
                            return self.finish_cancelled(turn_id, history, sink);
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
        let mut worker = Some(std::thread::spawn(move || {
            let result = provider.stream_chat(&request, &worker_token, &mut |event| {
                let _ = sender.send(ProviderMessage::Event(event));
            });
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
    fn finish_cancelled(
        &self,
        turn_id: &str,
        history: &mut Vec<ChatMessage>,
        sink: &dyn EventSink,
    ) -> Result<TurnResult, AgentError> {
        history.push(ChatMessage::assistant_with_text(
            "Cancelled by user.",
            Vec::new(),
        ));
        sink.emit(CoreEvent::TurnCompleted {
            turn_id: turn_id.to_owned(),
            status: TurnStatus::Cancelled.as_str().to_owned(),
            text: "Cancelled by user.".to_owned(),
        })?;
        Ok(TurnResult {
            status: TurnStatus::Cancelled,
            text: "Cancelled by user.".to_owned(),
        })
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

        let request: PolicyRequest = match tool.policy_request(&args) {
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

        let evaluation = self.policy.evaluate(request.action, &request.resource);
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
            if let Err(error) = sink.emit(CoreEvent::ToolApprovalRequested {
                turn_id: turn_id.to_owned(),
                call_id: call.id.clone(),
                approval_id: approval_id.clone(),
                name: call.name.clone(),
                action: request.action.as_str().to_owned(),
                resource: request.resource.clone(),
                args: args.clone(),
                reason: evaluation.reason.clone(),
            }) {
                return error_payload(&format!("event sink error: {error}"));
            }
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
                let output = truncate(&outcome.output, self.config.max_tool_output_bytes);
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
fn system_prompt(workspace: &std::path::Path) -> String {
    format!(
        "You are KL, a terminal coding agent. Work only inside the workspace at {}.\n\
         Inspect before editing. Use read_file, grep and glob to gather evidence; use edit_file \
         for exact replacements and write_file for new files. Use shell for builds/tests and git \
         for repository operations. Keep changes minimal, match existing style, and verify with \
         tests when practical. Never claim success without evidence.",
        workspace.display()
    )
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

#[cfg(test)]
mod tests {
    use super::*;
    use kl_audit::MemoryAudit;
    use kl_policy::PolicyConfig;
    use kl_provider::{FinishReason, ModelInfo, Protocol, ScriptedProvider, Usage};
    use std::sync::Mutex;
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
