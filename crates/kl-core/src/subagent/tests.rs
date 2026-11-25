use super::*;
use kl_policy::PolicyConfig;
use kl_provider::{ModelMetadata, Protocol, Provider, ScriptedProvider};
use kl_tools::ReadFileTool;
use std::path::Path;
use std::time::Duration;

fn tool_with_provider(
    dir: &Path,
    provider: ScriptedProvider,
) -> (Arc<SubagentTool>, Arc<AuditLog>, Arc<ToolRegistry>) {
    let metadata = ModelMetadata::new(
        "scripted",
        "scripted",
        Protocol::Scripted,
        "offline://scripted",
        8192,
        0.8,
        4096,
    );
    let router = ProviderRouter::new(
        vec![(metadata, Arc::new(provider) as Arc<dyn Provider>)],
        "scripted",
        "scripted",
    );
    let tools = Arc::new(ToolRegistry::empty());
    tools.register(Arc::new(ReadFileTool));
    let audit = Arc::new(AuditLog::open(dir.join("audit"), 1024 * 1024, 2).unwrap());
    let tool = Arc::new(SubagentTool::new(
        router,
        tools.clone(),
        Arc::new(SandboxManager::policy_only()),
        audit.clone(),
        AgentConfig::default(),
    ));
    tools.register(tool.clone());
    (tool, audit, tools)
}

#[test]
fn prompt_must_be_a_string() {
    let temp = tempfile::tempdir().unwrap();
    let (tool, _, _) = tool_with_provider(temp.path(), ScriptedProvider::empty("scripted"));

    assert!(matches!(
        tool.validate(&json!({})),
        Err(ToolError::InvalidArgs(_))
    ));
    assert!(matches!(
        tool.validate(&json!({"prompt": 1})),
        Err(ToolError::InvalidArgs(_))
    ));
    assert!(matches!(
        tool.policy_request(&json!({})),
        Err(ToolError::InvalidArgs(_))
    ));
    assert!(tool.validate(&json!({"prompt": "inspect"})).is_ok());
    let request = tool.policy_request(&json!({"prompt": "inspect"})).unwrap();
    assert_eq!(request.action, Action::Plugin);
    assert_eq!(request.resource, "subagent");
}

#[test]
fn child_registry_excludes_subagent_itself() {
    let temp = tempfile::tempdir().unwrap();
    let (tool, _, _) = tool_with_provider(temp.path(), ScriptedProvider::empty("scripted"));
    let names = tool.child_registry().names();

    assert!(names.iter().any(|name| name == "read_file"));
    assert!(!names.iter().any(|name| name == "subagent"));
}

#[test]
fn child_runs_read_only_and_records_child_audit() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::write(temp.path().join("note.txt"), "hello from child\n").unwrap();
    let provider = ScriptedProvider::from_json(
            "scripted",
            r#"[{"tool_calls":[{"id":"child-read","name":"read_file","arguments":{"path":"note.txt"}}]},{"text":"child answer"}]"#,
        )
        .unwrap();
    let (tool, audit, _) = tool_with_provider(temp.path(), provider);
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = ToolContext {
        policy: &policy,
        cancel: &cancel,
        sandbox: Arc::new(SandboxManager::policy_only()),
        timeout: Duration::from_secs(20),
        max_output_bytes: 64 * 1024,
    };

    let outcome = tool
        .execute(&context, &json!({"prompt": "inspect note.txt"}))
        .unwrap();
    assert_eq!(outcome.summary, "subagent completed");
    assert!(outcome.output.contains("child answer"));
    assert_eq!(audit.count().unwrap(), 1);

    let records = audit.tail(1).unwrap();
    assert_eq!(records[0]["session_id"].as_str(), Some("subagent"));
    assert_eq!(records[0]["tool"].as_str(), Some("read_file"));
    assert_eq!(records[0]["ok"].as_bool(), Some(true));
}
