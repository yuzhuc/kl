//! Local trusted hook configuration. Hooks can deny or fail a tool call, but
//! policy and approval still run first; hooks never grant permissions.

use kl_provider::ToolDefinition;
use kl_tools::{
    InterruptBehavior, PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, ToolSource,
};
use serde::Deserialize;
use serde_json::Value;
use std::fs::{File, OpenOptions};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitStatus, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const HOOK_TIMEOUT: Duration = Duration::from_secs(5);
const HOOK_OUTPUT_LIMIT: usize = 4 * 1024;
static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct HookConfig {
    pub hooks: HookSet,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct HookSet {
    pub pre_tool: Vec<String>,
    pub post_tool: Vec<String>,
}

impl HookConfig {
    pub fn load(path: &Path) -> Result<Self, String> {
        let text = match std::fs::read_to_string(path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(Self::default());
            }
            Err(error) => {
                return Err(format!("cannot read {}: {error}", path.display()));
            }
        };
        toml::from_str(&text).map_err(|error| format!("cannot parse {}: {error}", path.display()))
    }

    pub fn is_empty(&self) -> bool {
        self.hooks.pre_tool.is_empty() && self.hooks.post_tool.is_empty()
    }
}

#[derive(Debug, Clone)]
pub struct HookRunner {
    hooks: HookSet,
}

impl HookRunner {
    pub fn new(config: HookConfig) -> Self {
        Self {
            hooks: config.hooks,
        }
    }

    pub fn run_pre(&self, tool: &str, args: &Value, workspace: &Path) -> Result<(), String> {
        self.run(
            &self.hooks.pre_tool,
            "pre_tool",
            tool,
            args,
            workspace,
            None,
        )
    }

    #[allow(dead_code)]
    pub fn run_post(
        &self,
        tool: &str,
        outcome: &ToolOutcome,
        workspace: &Path,
    ) -> Result<(), String> {
        self.run_post_with_args(tool, &Value::Null, outcome, workspace)
    }

    fn run_post_with_args(
        &self,
        tool: &str,
        args: &Value,
        outcome: &ToolOutcome,
        workspace: &Path,
    ) -> Result<(), String> {
        self.run(
            &self.hooks.post_tool,
            "post_tool",
            tool,
            args,
            workspace,
            Some(outcome),
        )
    }

    fn run(
        &self,
        hooks: &[String],
        event: &str,
        tool: &str,
        args: &Value,
        workspace: &Path,
        outcome: Option<&ToolOutcome>,
    ) -> Result<(), String> {
        for hook in hooks {
            run_hook(hook, event, tool, args, workspace, outcome)?;
        }
        Ok(())
    }
}

pub struct HookedTool {
    inner: Arc<dyn Tool>,
    runner: Arc<HookRunner>,
}

impl HookedTool {
    pub fn new(inner: Arc<dyn Tool>, runner: Arc<HookRunner>) -> Self {
        Self { inner, runner }
    }
}

impl Tool for HookedTool {
    fn name(&self) -> &str {
        self.inner.name()
    }

    fn description(&self) -> &str {
        self.inner.description()
    }

    fn parameters(&self) -> Value {
        self.inner.parameters()
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        self.inner.policy_request(args)
    }

    fn policy_request_for(
        &self,
        workspace: &Path,
        args: &Value,
    ) -> Result<PolicyRequest, ToolError> {
        self.inner.policy_request_for(workspace, args)
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        self.inner.validate(args)
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        self.runner
            .run_pre(self.name(), args, context.policy.workspace())
            .map_err(|reason| ToolError::Policy(format!("hook denied: {reason}")))?;

        let outcome = self.inner.execute(context, args)?;
        self.runner
            .run_post_with_args(self.name(), args, &outcome, context.policy.workspace())
            .map_err(|reason| ToolError::Execution(format!("post hook denied: {reason}")))?;
        Ok(outcome)
    }

    fn read_only(&self, args: &Value) -> bool {
        self.inner.read_only(args)
    }

    fn destructive(&self, args: &Value) -> bool {
        self.inner.destructive(args)
    }

    fn concurrency_safe(&self, args: &Value) -> bool {
        self.inner.concurrency_safe(args)
    }

    fn interrupt_behavior(&self) -> InterruptBehavior {
        self.inner.interrupt_behavior()
    }

    fn max_result_chars(&self) -> usize {
        self.inner.max_result_chars()
    }

    fn source(&self) -> ToolSource {
        self.inner.source()
    }

    fn definition(&self) -> ToolDefinition {
        self.inner.definition()
    }
}

fn run_hook(
    script: &str,
    event: &str,
    tool: &str,
    args: &Value,
    workspace: &Path,
    outcome: Option<&ToolOutcome>,
) -> Result<(), String> {
    let mut files = OutputFiles::new()?;
    let args = serde_json::to_string(args)
        .map_err(|error| format!("cannot encode tool arguments: {error}"))?;
    let mut command = shell_command(script);
    command
        .env("KL_HOOK_EVENT", event)
        .env("KL_TOOL_NAME", tool)
        .env("KL_TOOL_ARGS", args)
        .env("KL_WORKSPACE", workspace)
        .env(
            "KL_TOOL_OK",
            outcome.map_or("", |outcome| if outcome.success { "true" } else { "false" }),
        )
        .env(
            "KL_TOOL_SUMMARY",
            outcome.map_or("", |outcome| outcome.summary.as_str()),
        );
    let (stdout, stderr) = files.take_stdio()?;
    command.stdout(stdout).stderr(stderr);

    let mut child = command
        .spawn()
        .map_err(|error| format!("cannot start hook: {error}"))?;
    let deadline = Instant::now() + HOOK_TIMEOUT;
    let (status, timed_out) = loop {
        match child.try_wait() {
            Ok(Some(status)) => break (status, false),
            Ok(None) if Instant::now() >= deadline => {
                let _ = child.kill();
                let status = child
                    .wait()
                    .map_err(|error| format!("cannot wait for timed-out hook: {error}"))?;
                break (status, true);
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(10)),
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!("cannot wait for hook: {error}"));
            }
        }
    };

    let stdout = read_capped(&files.stdout);
    let stderr = read_capped(&files.stderr);
    if timed_out {
        return Err(stream_reason("hook timed out after 5s", &stdout, &stderr));
    }
    if !status.success() {
        return Err(exit_reason(status, &stdout, &stderr));
    }
    Ok(())
}

fn shell_command(script: &str) -> Command {
    #[cfg(windows)]
    {
        let mut command = Command::new("cmd");
        command.arg("/C").arg(script);
        command
    }
    #[cfg(not(windows))]
    {
        let mut command = Command::new("sh");
        command.arg("-c").arg(script);
        command
    }
}

fn exit_reason(status: ExitStatus, stdout: &str, stderr: &str) -> String {
    let message = match status.code() {
        Some(code) => format!("hook exited with code {code}"),
        None => "hook terminated without an exit code".to_owned(),
    };
    stream_reason(&message, stdout, stderr)
}

fn stream_reason(message: &str, stdout: &str, stderr: &str) -> String {
    let mut reason = message.to_owned();
    if !stdout.is_empty() {
        reason.push_str(&format!("; stdout: {stdout}"));
    }
    if !stderr.is_empty() {
        reason.push_str(&format!("; stderr: {stderr}"));
    }
    reason
}

fn read_capped(path: &Path) -> String {
    let Ok(file) = File::open(path) else {
        return String::new();
    };
    let mut bytes = Vec::with_capacity(HOOK_OUTPUT_LIMIT);
    if file
        .take(HOOK_OUTPUT_LIMIT as u64)
        .read_to_end(&mut bytes)
        .is_err()
    {
        return String::new();
    }
    String::from_utf8_lossy(&bytes).trim().to_owned()
}

struct OutputFiles {
    stdout: PathBuf,
    stderr: PathBuf,
    stdout_file: Option<File>,
    stderr_file: Option<File>,
}

impl OutputFiles {
    fn new() -> Result<Self, String> {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|duration| duration.as_nanos())
            .unwrap_or(0);
        let id = TEMP_COUNTER.fetch_add(1, Ordering::Relaxed);
        let base = std::env::temp_dir();
        let stdout = base.join(format!("kl-hook-{}-{stamp}-{id}.out", std::process::id()));
        let stderr = base.join(format!("kl-hook-{}-{stamp}-{id}.err", std::process::id()));
        let stdout_file = create_temp_file(&stdout)?;
        let stderr_file = match create_temp_file(&stderr) {
            Ok(file) => file,
            Err(error) => {
                let _ = std::fs::remove_file(&stdout);
                return Err(error);
            }
        };
        Ok(Self {
            stdout,
            stderr,
            stdout_file: Some(stdout_file),
            stderr_file: Some(stderr_file),
        })
    }

    fn take_stdio(&mut self) -> Result<(Stdio, Stdio), String> {
        let Some(stdout) = self.stdout_file.take() else {
            return Err("hook stdout capture was already consumed".to_owned());
        };
        let Some(stderr) = self.stderr_file.take() else {
            return Err("hook stderr capture was already consumed".to_owned());
        };
        Ok((Stdio::from(stdout), Stdio::from(stderr)))
    }
}

impl Drop for OutputFiles {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.stdout);
        let _ = std::fs::remove_file(&self.stderr);
    }
}

fn create_temp_file(path: &Path) -> Result<File, String> {
    OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|error| format!("cannot create hook capture {}: {error}", path.display()))
}

#[cfg(test)]
mod tests {
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

        fn execute(
            &self,
            _context: &ToolContext<'_>,
            _args: &Value,
        ) -> Result<ToolOutcome, ToolError> {
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
        assert!(
            matches!(error, ToolError::Execution(message) if message.contains("post hook denied"))
        );
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
}
