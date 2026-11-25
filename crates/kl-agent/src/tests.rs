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
fn home_instructions_read_the_home_agents_file() {
    let temp = tempfile::tempdir().unwrap();
    assert_eq!(home_instructions(temp.path()), None);
    std::fs::write(temp.path().join("AGENTS.md"), "Always answer in Chinese.\n").unwrap();
    assert_eq!(
        home_instructions(temp.path()).as_deref(),
        Some("Always answer in Chinese.\n")
    );
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

    fn execute(&self, _context: &ToolContext<'_>, _args: &Value) -> Result<ToolOutcome, ToolError> {
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

    fn execute(&self, _context: &ToolContext<'_>, _args: &Value) -> Result<ToolOutcome, ToolError> {
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

/// Read-only probe that reports how many copies of itself run at the same
/// time. Each call waits until `target` copies are in flight, so the test
/// fails (instead of hanging) when the worker pool is too small.
struct ConcurrencyProbe {
    in_flight: AtomicUsize,
    peak: AtomicUsize,
    target: usize,
}

impl ConcurrencyProbe {
    fn new(target: usize) -> Self {
        Self {
            in_flight: AtomicUsize::new(0),
            peak: AtomicUsize::new(0),
            target,
        }
    }
}

impl Tool for ConcurrencyProbe {
    fn name(&self) -> &str {
        "probe"
    }

    fn description(&self) -> &str {
        "concurrency probe"
    }

    fn parameters(&self) -> Value {
        serde_json::json!({"type": "object"})
    }

    fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Read,
            resource: "probe".to_owned(),
        })
    }

    fn read_only(&self, _args: &Value) -> bool {
        true
    }

    fn execute(&self, _context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let label = args
            .get("label")
            .and_then(Value::as_str)
            .unwrap_or("?")
            .to_owned();
        let current = self.in_flight.fetch_add(1, Ordering::SeqCst) + 1;
        self.peak.fetch_max(current, Ordering::SeqCst);
        let deadline = Instant::now() + Duration::from_secs(5);
        while self.in_flight.load(Ordering::SeqCst) < self.target && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(2));
        }
        self.in_flight.fetch_sub(1, Ordering::SeqCst);
        Ok(ToolOutcome::ok(
            format!("probe {label}"),
            format!("probe {label}"),
        ))
    }
}

#[test]
fn parallel_tool_batch_exceeds_four_workers_and_keeps_result_order() {
    let target = 8;
    let temp = tempfile::tempdir().unwrap();
    let probe = Arc::new(ConcurrencyProbe::new(target));
    let tools = ToolRegistry::empty();
    let tool: Arc<dyn Tool> = probe.clone();
    tools.register(tool);
    let policy = Arc::new(PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap());
    let agent = Agent::new(
        Arc::new(FailingProvider),
        metadata(),
        Arc::new(tools),
        policy,
        Arc::new(SandboxManager::policy_only()),
        Arc::new(MemoryAudit::default()),
        AgentConfig::default(),
    );
    let calls: Vec<ToolCall> = (0..target)
        .map(|index| ToolCall {
            id: format!("call-{index}"),
            name: "probe".to_owned(),
            arguments: format!("{{\"label\":\"{index}\"}}"),
        })
        .collect();
    let sink = RecordingSink::default();

    let results = agent.execute_tool_batch(
        "session",
        "turn",
        &calls,
        &AtomicBool::new(false),
        &sink,
        &Gate(ApprovalDecision::Allow),
    );

    assert_eq!(results.len(), target);
    for (index, payload) in results.iter().enumerate() {
        let payload: Value = serde_json::from_str(payload).unwrap();
        assert_eq!(payload["ok"], true, "call {index}: {payload}");
        assert_eq!(
            payload["output"].as_str(),
            Some(format!("probe {index}").as_str()),
            "call {index}: {payload}"
        );
    }
    let peak = probe.peak.load(Ordering::SeqCst);
    assert!(
        peak > 4,
        "expected more than 4 concurrent tool calls, peak was {peak}"
    );
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

    let (request, _) = agent.prepare_request(&mut history).unwrap();

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
    history.push(ChatMessage::user("latest request"));
    let original_len = history.len();

    let (request, compaction) = agent.prepare_request(&mut history).unwrap();

    assert!(compaction.is_some());
    assert_eq!(request.messages[0].text(), "persistent system");
    assert_eq!(request.messages.last().unwrap().text(), "latest request");
    assert!(
        request
            .messages
            .iter()
            .any(|message| compaction_omitted(message).is_some_and(|count| count > 0))
    );
    assert!(
        estimate_context_tokens(&request)
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
        let (request, _) = agent.prepare_request(&mut history).unwrap();
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
fn automatic_compaction_uses_summary_and_emits_an_event() {
    let provider = Arc::new(SummaryProvider {
        text: "The earlier work is summarized automatically.".to_owned(),
        requests: Mutex::new(Vec::new()),
    });
    let agent = test_agent(provider.clone(), metadata_with_limit(4_000, 0.9, 100));
    let mut history = oversized_history();
    let sink = RecordingSink::default();

    let result = agent
        .run_turn(
            "session",
            "turn-auto-compact",
            "continue",
            &mut history,
            Arc::new(AtomicBool::new(false)),
            &sink,
            &Gate(ApprovalDecision::Allow),
        )
        .unwrap();

    assert_eq!(result.text, "The earlier work is summarized automatically.");
    let events = sink.events.lock().unwrap();
    assert!(events.iter().any(|event| matches!(
        event,
        CoreEvent::ContextCompacted {
            trigger,
            summary_used: true,
            summary: Some(summary),
            ..
        } if trigger == "turn.auto" && summary.contains("summarized automatically")
    )));
    let requests = provider.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert_eq!(requests[0].system, SUMMARY_INSTRUCTIONS);
    assert!(requests[1].system.contains(SUMMARY_HEADER));
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
