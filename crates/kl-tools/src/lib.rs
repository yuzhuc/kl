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
use std::collections::{HashMap, HashSet};
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
    disabled: RwLock<HashSet<String>>,
}

impl ToolRegistry {
    pub fn empty() -> Self {
        Self {
            tools: RwLock::new(HashMap::new()),
            disabled: RwLock::new(HashSet::new()),
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
            disabled: RwLock::new(HashSet::new()),
        }
    }

    /// Hides a tool from Core and model definitions without dropping its
    /// registration, so toggling it back on needs no rebuild.
    pub fn set_enabled(&self, name: &str, enabled: bool) {
        let mut disabled = self
            .disabled
            .write()
            .unwrap_or_else(|error| error.into_inner());
        if enabled {
            disabled.remove(name);
        } else {
            disabled.insert(name.to_owned());
        }
    }

    fn is_disabled(&self, name: &str) -> bool {
        self.disabled
            .read()
            .unwrap_or_else(|error| error.into_inner())
            .contains(name)
    }

    pub fn register(&self, tool: Arc<dyn Tool>) {
        self.tools
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .insert(tool.name().to_owned(), tool);
    }

    pub fn remove(&self, name: &str) -> bool {
        self.disabled
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .remove(name);
        self.tools
            .write()
            .unwrap_or_else(|error| error.into_inner())
            .remove(name)
            .is_some()
    }

    pub fn get(&self, name: &str) -> Option<Arc<dyn Tool>> {
        if self.is_disabled(name) {
            return None;
        }
        self.tools
            .read()
            .unwrap_or_else(|error| error.into_inner())
            .get(name)
            .cloned()
    }

    pub fn snapshot(&self) -> Vec<Arc<dyn Tool>> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut snapshot: Vec<Arc<dyn Tool>> = tools
            .iter()
            .filter(|(name, _)| !self.is_disabled(name))
            .map(|(_, tool)| tool.clone())
            .collect();
        snapshot.sort_by(|left, right| left.name().cmp(right.name()));
        snapshot
    }

    pub fn definitions(&self) -> Vec<ToolDefinition> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut definitions: Vec<ToolDefinition> = tools
            .iter()
            .filter(|(name, _)| !self.is_disabled(name))
            .map(|(_, tool)| tool.definition())
            .collect();
        definitions.sort_by(|left, right| left.name.cmp(&right.name));
        definitions
    }

    pub fn names(&self) -> Vec<String> {
        let tools = self.tools.read().unwrap_or_else(|error| error.into_inner());
        let mut names: Vec<String> = tools
            .keys()
            .filter(|name| !self.is_disabled(name))
            .cloned()
            .collect();
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
mod tests;
