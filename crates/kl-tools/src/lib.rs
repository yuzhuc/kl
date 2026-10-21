//! Built-in tools. Tools evaluate policy again before touching the
//! filesystem so a direct call cannot bypass the agent's approval gate.

mod ask_user;
mod diff;
mod files;
mod jobs;
mod memory;
mod pty;
mod search;
mod shell;
mod todo;
mod web_fetch;
mod worktree;

pub use ask_user::AskUserTool;
pub use files::{EditFileTool, ReadFileTool, WriteFileTool};
pub use jobs::JobTool;
pub use memory::{MemoryEntry, MemoryInjection, MemoryScope, MemoryStore, MemoryTool};
pub use search::{GlobTool, GrepTool};
pub use shell::{GitTool, ShellTool};
pub use todo::TodoTool;
pub use web_fetch::WebFetchTool;
pub use worktree::WorktreeTool;

use kl_policy::{Action, PolicyEngine};
use kl_provider::ToolDefinition;
use kl_sandbox::SandboxManager;
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, RwLock};
use std::time::Duration;

pub struct ToolContext<'a> {
    pub policy: &'a PolicyEngine,
    pub cancel: &'a AtomicBool,
    pub sandbox: Arc<SandboxManager>,
    pub timeout: Duration,
    pub max_output_bytes: usize,
}

#[derive(Debug, Clone)]
pub struct PolicyRequest {
    pub action: Action,
    pub resource: String,
}

#[derive(Debug, Clone)]
pub struct ToolOutcome {
    pub success: bool,
    pub summary: String,
    pub output: String,
    pub diff: Option<String>,
}

impl ToolOutcome {
    pub fn ok(summary: impl Into<String>, output: impl Into<String>) -> Self {
        Self {
            success: true,
            summary: summary.into(),
            output: output.into(),
            diff: None,
        }
    }

    pub fn with_diff(mut self, diff: Option<String>) -> Self {
        self.diff = diff;
        self
    }
}

#[derive(Debug, thiserror::Error)]
pub enum ToolError {
    #[error("invalid arguments: {0}")]
    InvalidArgs(String),
    #[error("policy denied: {0}")]
    Policy(String),
    #[error("io error: {0}")]
    Io(String),
    #[error("execution error: {0}")]
    Execution(String),
    #[error("sandbox violation: {0}")]
    Sandbox(kl_sandbox::SandboxViolation),
    #[error("not found: {0}")]
    NotFound(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InterruptBehavior {
    Cancel,
    Continue,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ToolSource {
    Builtin,
    Sidecar,
    Wasm,
    Mcp,
}

pub trait Tool: Send + Sync {
    fn name(&self) -> &str;
    fn description(&self) -> &str;
    fn parameters(&self) -> Value;
    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError>;
    fn policy_request_for(
        &self,
        _workspace: &Path,
        args: &Value,
    ) -> Result<PolicyRequest, ToolError> {
        self.policy_request(args)
    }
    fn validate(&self, _args: &Value) -> Result<(), ToolError> {
        Ok(())
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError>;

    fn read_only(&self, _args: &Value) -> bool {
        false
    }

    fn destructive(&self, _args: &Value) -> bool {
        false
    }

    fn concurrency_safe(&self, args: &Value) -> bool {
        self.read_only(args)
    }

    /// Contract flag for tools that belong outside the current turn's tool
    /// batch. Recorded on the trait for now; the agent does not act on it yet.
    fn should_defer(&self, _args: &Value) -> bool {
        false
    }

    /// Tools that must always ask the user, even when policy would allow them.
    fn always_ask(&self) -> bool {
        false
    }

    fn interrupt_behavior(&self) -> InterruptBehavior {
        InterruptBehavior::Cancel
    }

    fn max_result_chars(&self) -> usize {
        30_000
    }

    fn source(&self) -> ToolSource {
        ToolSource::Builtin
    }

    fn definition(&self) -> ToolDefinition {
        ToolDefinition {
            name: self.name().to_owned(),
            description: self.description().to_owned(),
            parameters: self.parameters(),
        }
    }
}

pub struct ToolRegistry {
    tools: RwLock<HashMap<String, Arc<dyn Tool>>>,
}

impl ToolRegistry {
    pub fn empty() -> Self {
        Self {
            tools: RwLock::new(HashMap::new()),
        }
    }

    pub fn builtin() -> Self {
        let tools: Vec<Arc<dyn Tool>> = vec![
            Arc::new(ReadFileTool),
            Arc::new(WriteFileTool),
            Arc::new(EditFileTool),
            Arc::new(GrepTool),
            Arc::new(GlobTool),
            Arc::new(ShellTool),
            Arc::new(GitTool),
            Arc::new(WorktreeTool),
            Arc::new(JobTool::new()),
            Arc::new(MemoryTool),
            Arc::new(TodoTool),
            Arc::new(AskUserTool),
            Arc::new(WebFetchTool),
        ];
        Self {
            tools: RwLock::new(
                tools
                    .into_iter()
                    .map(|tool| (tool.name().to_owned(), tool))
                    .collect(),
            ),
        }
    }

    pub fn register(&self, tool: Arc<dyn Tool>) {
        self.tools
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .insert(tool.name().to_owned(), tool);
    }

    pub fn remove(&self, name: &str) -> bool {
        self.tools
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .remove(name)
            .is_some()
    }

    pub fn get(&self, name: &str) -> Option<Arc<dyn Tool>> {
        self.tools
            .read()
            .unwrap_or_else(|error| error.into_inner())
            .get(name)
            .cloned()
    }

    pub fn snapshot(&self) -> Vec<Arc<dyn Tool>> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut snapshot: Vec<Arc<dyn Tool>> = tools.values().cloned().collect();
        snapshot.sort_by(|left, right| left.name().cmp(right.name()));
        snapshot
    }

    pub fn definitions(&self) -> Vec<ToolDefinition> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut definitions: Vec<ToolDefinition> =
            tools.values().map(|tool| tool.definition()).collect();
        definitions.sort_by(|left, right| left.name.cmp(&right.name));
        definitions
    }

    pub fn names(&self) -> Vec<String> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut names: Vec<String> = tools.keys().cloned().collect();
        names.sort_unstable();
        names
    }
}

fn string_arg<'a>(args: &'a Value, name: &str) -> Result<&'a str, ToolError> {
    args.get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| ToolError::InvalidArgs(format!("`{name}` must be a string")))
}

fn optional_string<'a>(args: &'a Value, name: &str) -> Option<&'a str> {
    args.get(name).and_then(Value::as_str)
}

fn optional_usize(args: &Value, name: &str) -> Option<usize> {
    args.get(name)
        .and_then(Value::as_u64)
        .map(|value| value as usize)
}

fn optional_bool(args: &Value, name: &str) -> bool {
    args.get(name).and_then(Value::as_bool).unwrap_or(false)
}

fn truncate_text(text: &str, limit: usize) -> String {
    if text.len() <= limit {
        return text.to_owned();
    }
    let mut end = limit;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}\n... [output truncated at {limit} bytes]", &text[..end])
}

#[cfg(test)]
mod tests {
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
}
