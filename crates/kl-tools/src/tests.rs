use super::*;
use kl_policy::PolicyConfig;
use serde_json::json;
use std::sync::atomic::AtomicBool;

fn context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
    ToolContext {
        policy,
        cancel,
        sandbox: Arc::new(SandboxManager::policy_only()),
        timeout: Duration::from_secs(20),
        max_output_bytes: 64 * 1024,
    }
}

#[test]
fn builtin_tool_metadata() {
    let registry = ToolRegistry::builtin();

    for name in ["read_file", "grep", "glob"] {
        let tool = registry.get(name).unwrap();
        assert!(tool.read_only(&json!({})), "{name}");
        assert!(tool.concurrency_safe(&json!({})), "{name}");
        assert_eq!(tool.source(), ToolSource::Builtin);
    }

    let job = registry.get("job").unwrap();
    assert!(job.read_only(&json!({"action": "status", "job_id": "job-1"})));
    assert!(job.read_only(&json!({"action": "list"})));

    let memory = registry.get("memory").unwrap();
    for args in [
        json!({"action": "list"}),
        json!({"action": "read", "name": "notes.md"}),
    ] {
        assert!(memory.read_only(&args), "{args}");
        assert!(memory.concurrency_safe(&args), "{args}");
        assert!(!memory.destructive(&args), "{args}");
    }
    for args in [
        json!({"action": "write", "name": "notes.md", "content": "x"}),
        json!({"action": "delete", "name": "notes.md"}),
    ] {
        assert!(!memory.read_only(&args), "{args}");
        assert!(!memory.concurrency_safe(&args), "{args}");
        assert!(memory.destructive(&args), "{args}");
    }

    let todo = registry.get("todo").unwrap();
    for args in [
        json!({"action": "add", "title": "write tests"}),
        json!({"action": "update", "id": "t1", "title": "write tests"}),
        json!({"action": "complete", "id": "t1"}),
    ] {
        assert!(!todo.read_only(&args), "{args}");
        assert!(!todo.concurrency_safe(&args), "{args}");
        assert!(todo.destructive(&args), "{args}");
    }
    let listed = json!({"action": "list"});
    assert!(todo.read_only(&listed));
    assert!(todo.concurrency_safe(&listed));
    assert!(!todo.destructive(&listed));

    for name in ["write_file", "edit_file", "shell", "git", "worktree", "job"] {
        let tool = registry.get(name).unwrap();
        assert!(tool.destructive(&json!({})), "{name}");
        assert!(!tool.read_only(&json!({})), "{name}");
        assert!(!tool.concurrency_safe(&json!({})), "{name}");
        assert_eq!(tool.source(), ToolSource::Builtin);
    }

    for name in registry.names() {
        let tool = registry.get(&name).unwrap();
        assert!(!tool.should_defer(&json!({})), "{name}");
    }
}

#[test]
fn read_write_edit_round_trip() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(
        temp.path(),
        PolicyConfig {
            auto_approve_edits: true,
            auto_approve_shell: true,
            ..PolicyConfig::default()
        },
    )
    .unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);

    let write = WriteFileTool
        .execute(
            &context,
            &json!({"path": "src/main.rs", "content": "fn main() {}\n"}),
        )
        .unwrap();
    assert!(write.success);
    assert!(write.diff.unwrap().contains("fn main"));

    let read = ReadFileTool
        .execute(&context, &json!({"path": "src/main.rs"}))
        .unwrap();
    assert!(read.output.contains("fn main"));

    let edit = EditFileTool
        .execute(
            &context,
            &json!({
                "path": "src/main.rs",
                "old_string": "fn main()",
                "new_string": "fn kl_main()"
            }),
        )
        .unwrap();
    assert!(edit.success);
    assert!(edit.diff.unwrap().contains("kl_main"));
}

#[test]
fn path_escape_is_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(
        temp.path(),
        PolicyConfig {
            auto_approve_edits: true,
            ..PolicyConfig::default()
        },
    )
    .unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);
    let error = ReadFileTool
        .execute(&context, &json!({"path": "../../secret.txt"}))
        .unwrap_err();
    assert!(matches!(error, ToolError::Policy(_)));
}

#[test]
fn grep_and_glob_find_files() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(temp.path().join("src")).unwrap();
    std::fs::write(temp.path().join("src/a.rs"), "fn alpha() {}\n").unwrap();
    std::fs::write(temp.path().join("src/b.txt"), "alpha\n").unwrap();
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);

    let grep = GrepTool
        .execute(
            &context,
            &json!({"pattern": "alpha", "path": ".", "glob": "*.rs"}),
        )
        .unwrap();
    assert!(grep.output.contains("src/a.rs"));
    assert!(!grep.output.contains("b.txt"));

    let glob = GlobTool
        .execute(&context, &json!({"pattern": "*.rs"}))
        .unwrap();
    assert!(glob.output.contains("src/a.rs"));
}

#[test]
fn shell_env_allowlist_hides_unlisted_variables() {
    unsafe {
        std::env::set_var("KL_TOOL_TEST_ALLOWED", "visible-value");
        std::env::set_var("KL_TOOL_TEST_DENIED", "hidden-value");
    }
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(
        temp.path(),
        PolicyConfig {
            auto_approve_shell: true,
            env_allowlist: vec!["KL_TOOL_TEST_ALLOWED".to_owned()],
            ..PolicyConfig::default()
        },
    )
    .unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);
    let command = if cfg!(windows) {
        "echo [%KL_TOOL_TEST_ALLOWED%] [%KL_TOOL_TEST_DENIED%]"
    } else {
        "printf '[%s] [%s]' \"$KL_TOOL_TEST_ALLOWED\" \"$KL_TOOL_TEST_DENIED\""
    };
    let outcome = ShellTool
        .execute(&context, &json!({"command": command}))
        .unwrap();
    assert!(outcome.output.contains("visible-value"));
    assert!(!outcome.output.contains("hidden-value"));
}

#[test]
fn shell_runs_and_reports_output() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(
        temp.path(),
        PolicyConfig {
            auto_approve_shell: true,
            ..PolicyConfig::default()
        },
    )
    .unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);
    let command = if cfg!(windows) {
        "echo hello"
    } else {
        "printf hello"
    };
    let outcome = ShellTool
        .execute(&context, &json!({"command": command}))
        .unwrap();
    assert!(outcome.success);
    assert!(outcome.output.contains("hello"));
}

#[test]
fn git_status_runs() {
    if std::process::Command::new("git")
        .arg("--version")
        .output()
        .is_err()
    {
        return;
    }
    let temp = tempfile::tempdir().unwrap();
    std::process::Command::new("git")
        .args(["init", "-q"])
        .current_dir(temp.path())
        .status()
        .unwrap();
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = context(&policy, &cancel);
    let outcome = GitTool
        .execute(&context, &json!({"args": ["status", "--short"]}))
        .unwrap();
    assert!(outcome.success);
}
