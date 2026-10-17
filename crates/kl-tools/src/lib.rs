//! Built-in tools. Tools evaluate policy again before touching the
//! filesystem so a direct call cannot bypass the agent's approval gate.

mod diff;
mod files;
mod search;
mod shell;

pub use files::{EditFileTool, ReadFileTool, WriteFileTool};
pub use search::{GlobTool, GrepTool};
pub use shell::{GitTool, ShellTool};

use kl_policy::{Action, PolicyEngine};
use kl_provider::ToolDefinition;
use serde_json::Value;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::time::Duration;

pub struct ToolContext<'a> {
    pub policy: &'a PolicyEngine,
    pub cancel: &'a AtomicBool,
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
    #[error("not found: {0}")]
    NotFound(String),
}

pub trait Tool: Send + Sync {
    fn name(&self) -> &'static str;
    fn description(&self) -> &'static str;
    fn parameters(&self) -> Value;
    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError>;
    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError>;

    fn definition(&self) -> ToolDefinition {
        ToolDefinition {
            name: self.name().to_owned(),
            description: self.description().to_owned(),
            parameters: self.parameters(),
        }
    }
}

pub struct ToolRegistry {
    tools: HashMap<&'static str, Arc<dyn Tool>>,
}

impl ToolRegistry {
    pub fn builtin() -> Self {
        let tools: Vec<Arc<dyn Tool>> = vec![
            Arc::new(ReadFileTool),
            Arc::new(WriteFileTool),
            Arc::new(EditFileTool),
            Arc::new(GrepTool),
            Arc::new(GlobTool),
            Arc::new(ShellTool),
            Arc::new(GitTool),
        ];
        Self {
            tools: tools.into_iter().map(|tool| (tool.name(), tool)).collect(),
        }
    }

    pub fn get(&self, name: &str) -> Option<Arc<dyn Tool>> {
        self.tools.get(name).cloned()
    }

    pub fn definitions(&self) -> Vec<ToolDefinition> {
        let mut definitions: Vec<ToolDefinition> =
            self.tools.values().map(|tool| tool.definition()).collect();
        definitions.sort_by(|left, right| left.name.cmp(&right.name));
        definitions
    }

    pub fn names(&self) -> Vec<&'static str> {
        let mut names: Vec<&'static str> = self.tools.keys().copied().collect();
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
            timeout: Duration::from_secs(20),
            max_output_bytes: 64 * 1024,
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
