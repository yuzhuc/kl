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
