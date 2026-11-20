use super::*;
use kl_policy::{Action, PolicyConfig, PolicyEngine};
use kl_sandbox::SandboxManager;
use serde_json::json;
use std::sync::atomic::{AtomicBool, AtomicUsize};

#[derive(Clone)]
struct CountingTool {
    calls: Arc<AtomicUsize>,
}

impl CountingTool {
    fn new() -> Self {
        Self {
            calls: Arc::new(AtomicUsize::new(0)),
        }
    }
}

impl Tool for CountingTool {
    fn name(&self) -> &str {
        "count"
    }

    fn description(&self) -> &str {
        "count calls"
    }

    fn parameters(&self) -> Value {
        json!({"type": "object"})
    }

    fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Read,
            resource: ".".to_owned(),
        })
    }

    fn execute(&self, _context: &ToolContext<'_>, _args: &Value) -> Result<ToolOutcome, ToolError> {
        self.calls.fetch_add(1, Ordering::Relaxed);
        Ok(ToolOutcome::ok("ok", "result"))
    }
}

fn runner(pre_tool: Vec<String>, post_tool: Vec<String>) -> HookRunner {
    HookRunner::new(HookConfig {
        hooks: HookSet {
            pre_tool,
            post_tool,
        },
    })
}

fn exit_hook(code: i32) -> String {
    #[cfg(windows)]
    {
        format!("cmd /C exit {code}")
    }
    #[cfg(not(windows))]
    {
        format!("exit {code}")
    }
}

fn tool_context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
    ToolContext {
        policy,
        cancel,
        sandbox: Arc::new(SandboxManager::policy_only()),
        timeout: Duration::from_secs(20),
        max_output_bytes: 64 * 1024,
    }
}

#[test]
fn empty_config_succeeds() {
    let runner = runner(Vec::new(), Vec::new());
    let outcome = ToolOutcome::ok("ok", "result");
    assert!(runner.run_pre("count", &json!({}), Path::new(".")).is_ok());
    assert!(runner.run_post("count", &outcome, Path::new(".")).is_ok());
}

#[test]
fn missing_config_file_is_empty() {
    let temp = tempfile::tempdir().unwrap();
    let config = HookConfig::load(&temp.path().join("hooks.toml")).unwrap();
    assert!(config.is_empty());
}

#[test]
fn pre_hook_failure_does_not_execute_inner_tool() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = tool_context(&policy, &cancel);
    let inner = CountingTool::new();
    let wrapped = HookedTool::new(
        Arc::new(inner.clone()),
        Arc::new(runner(vec![exit_hook(1)], Vec::new())),
    );

    let error = wrapped.execute(&context, &json!({})).unwrap_err();
    assert!(matches!(error, ToolError::Policy(message) if message.contains("hook denied")));
    assert_eq!(inner.calls.load(Ordering::Relaxed), 0);
}

#[test]
fn post_hook_failure_returns_execution_error() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = tool_context(&policy, &cancel);
    let inner = CountingTool::new();
    let wrapped = HookedTool::new(
        Arc::new(inner.clone()),
        Arc::new(runner(Vec::new(), vec![exit_hook(1)])),
    );

    let error = wrapped.execute(&context, &json!({})).unwrap_err();
    assert!(matches!(error, ToolError::Execution(message) if message.contains("post hook denied")));
    assert_eq!(inner.calls.load(Ordering::Relaxed), 1);
}

#[test]
fn successful_hooks_preserve_tool_result() {
    let temp = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let context = tool_context(&policy, &cancel);
    let inner = CountingTool::new();
    let wrapped = HookedTool::new(
        Arc::new(inner.clone()),
        Arc::new(runner(vec![exit_hook(0)], vec![exit_hook(0)])),
    );

    let outcome = wrapped.execute(&context, &json!({})).unwrap();
    assert_eq!(outcome.summary, "ok");
    assert_eq!(outcome.output, "result");
    assert_eq!(inner.calls.load(Ordering::Relaxed), 1);
}

#[test]
fn hook_timeout_is_reported() {
    let timeout_hook = if cfg!(windows) {
        "ping -n 6 127.0.0.1 >nul"
    } else {
        "sleep 6"
    };
    let runner = runner(vec![timeout_hook.to_owned()], Vec::new());
    let started = Instant::now();
    let error = runner
        .run_pre("count", &json!({}), Path::new("."))
        .unwrap_err();
    assert!(error.contains("timed out"));
    assert!(started.elapsed() < Duration::from_secs(10));
}
