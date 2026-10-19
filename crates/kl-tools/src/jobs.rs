use crate::shell::{apply_env_policy, resolve_cwd};
use crate::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome};
use kl_exec::CommandSpec;
use kl_policy::{Action, Effect};
use kl_sandbox::{SandboxChild, SandboxPolicy};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::Read;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;

const JOB_OUTPUT_LIMIT: usize = 32 * 1024;

pub struct JobTool {
    jobs: Mutex<BTreeMap<String, Job>>,
    next_id: AtomicU64,
}

impl Default for JobTool {
    fn default() -> Self {
        Self::new()
    }
}

impl JobTool {
    pub fn new() -> Self {
        Self {
            jobs: Mutex::new(BTreeMap::new()),
            next_id: AtomicU64::new(1),
        }
    }

    fn jobs(&self) -> std::sync::MutexGuard<'_, BTreeMap<String, Job>> {
        self.jobs.lock().unwrap_or_else(|error| error.into_inner())
    }
}

struct Job {
    child: SandboxChild,
    output: Arc<Mutex<Vec<u8>>>,
    state: JobState,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum JobState {
    Running,
    Exited(i32),
    Stopped,
}

impl JobState {
    fn label(self) -> &'static str {
        match self {
            Self::Running => "running",
            Self::Exited(_) => "exited",
            Self::Stopped => "stopped",
        }
    }

    fn exit_code(self) -> Option<i32> {
        match self {
            Self::Exited(code) => Some(code),
            _ => None,
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum JobAction {
    Start,
    Status,
    Stop,
    List,
}

impl JobAction {
    fn parse(value: &str) -> Result<Self, ToolError> {
        match value {
            "start" => Ok(Self::Start),
            "status" => Ok(Self::Status),
            "stop" => Ok(Self::Stop),
            "list" => Ok(Self::List),
            other => Err(ToolError::InvalidArgs(format!(
                "`action` must be start, status, stop or list (got {other})"
            ))),
        }
    }

    fn read_only(self) -> bool {
        matches!(self, Self::Status | Self::List)
    }
}

impl Tool for JobTool {
    fn name(&self) -> &'static str {
        "job"
    }

    fn description(&self) -> &'static str {
        "Start, inspect and stop background shell commands. Jobs are sandboxed and keep running between turns."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["start", "status", "stop", "list"]
                },
                "command": {"type": "string"},
                "cwd": {"type": "string", "default": "."},
                "job_id": {"type": "string"}
            },
            "required": ["action"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let action = parse_action(args)?;
        match action {
            JobAction::Start => Ok(PolicyRequest {
                action: Action::Shell,
                resource: required(args, "command")?.to_owned(),
            }),
            JobAction::Stop | JobAction::Status => Ok(PolicyRequest {
                action: if action == JobAction::Stop {
                    Action::Shell
                } else {
                    Action::Read
                },
                resource: format!("job:{}", required(args, "job_id")?),
            }),
            JobAction::List => Ok(PolicyRequest {
                action: Action::Read,
                resource: "jobs".to_owned(),
            }),
        }
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        let action = parse_action(args)?;
        match action {
            JobAction::Start => {
                required(args, "command")?;
                optional(args, "cwd")?;
            }
            JobAction::Status | JobAction::Stop => {
                required(args, "job_id")?;
            }
            JobAction::List => {}
        }
        Ok(())
    }

    fn read_only(&self, args: &Value) -> bool {
        parse_action(args).is_ok_and(JobAction::read_only)
    }

    fn destructive(&self, _args: &Value) -> bool {
        true
    }

    fn max_result_chars(&self) -> usize {
        JOB_OUTPUT_LIMIT
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let action = parse_action(args)?;
        let request = self.policy_request(args)?;
        let evaluation = context.policy.evaluate(request.action, &request.resource);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }

        match action {
            JobAction::Start => self.start(context, args),
            JobAction::Status => self.status(required(args, "job_id")?),
            JobAction::Stop => self.stop(required(args, "job_id")?),
            JobAction::List => self.list(),
        }
    }
}

impl JobTool {
    fn start(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let command = required(args, "command")?;
        let cwd = resolve_cwd(context, optional(args, "cwd")?.unwrap_or("."))?;
        let mut spec = if cfg!(windows) {
            CommandSpec::new("cmd", vec!["/C".to_owned(), command.to_owned()], cwd)
        } else {
            CommandSpec::new("sh", vec!["-c".to_owned(), command.to_owned()], cwd)
        };
        spec.max_output_bytes = JOB_OUTPUT_LIMIT;
        apply_env_policy(context.policy, &mut spec);

        let policy = SandboxPolicy::workspace(context.policy.workspace());
        let mut child = context
            .sandbox
            .spawn(&spec, &policy)
            .map_err(map_sandbox_error)?;
        // Jobs never read stdin; dropping it keeps the child from waiting on input.
        drop(child.stdin.take());
        let output = Arc::new(Mutex::new(Vec::new()));
        if let Some(stdout) = child.stdout.take() {
            let buffer = Arc::clone(&output);
            thread::spawn(move || drain(stdout, buffer));
        }
        if let Some(stderr) = child.stderr.take() {
            let buffer = Arc::clone(&output);
            thread::spawn(move || drain(stderr, buffer));
        }

        let id = format!("job-{}", self.next_id.fetch_add(1, Ordering::Relaxed));
        let pid = child.id();
        self.jobs().insert(
            id.clone(),
            Job {
                child,
                output,
                state: JobState::Running,
            },
        );
        Ok(ToolOutcome::ok(
            format!("started {id}"),
            format!("{id} running (pid {pid})"),
        ))
    }

    fn status(&self, id: &str) -> Result<ToolOutcome, ToolError> {
        let mut jobs = self.jobs();
        let job = jobs
            .get_mut(id)
            .ok_or_else(|| ToolError::NotFound(format!("job {id}")))?;
        refresh(job)?;
        Ok(ToolOutcome::ok(
            format!("{id} {}", job.state.label()),
            render_job(id, job),
        ))
    }

    fn stop(&self, id: &str) -> Result<ToolOutcome, ToolError> {
        let mut jobs = self.jobs();
        let job = jobs
            .get_mut(id)
            .ok_or_else(|| ToolError::NotFound(format!("job {id}")))?;
        if job.state == JobState::Running {
            job.child.kill().map_err(map_sandbox_error)?;
            let _ = job.child.wait();
            job.state = JobState::Stopped;
        }
        Ok(ToolOutcome::ok(
            format!("{id} {}", job.state.label()),
            render_job(id, job),
        ))
    }

    fn list(&self) -> Result<ToolOutcome, ToolError> {
        let mut jobs = self.jobs();
        if jobs.is_empty() {
            return Ok(ToolOutcome::ok("no jobs", "no jobs"));
        }
        let mut lines = Vec::with_capacity(jobs.len());
        for (id, job) in jobs.iter_mut() {
            refresh(job)?;
            lines.push(format!("{id}: {}", job.state.label()));
        }
        let text = lines.join("\n");
        Ok(ToolOutcome::ok(format!("{} jobs", jobs.len()), text))
    }
}

fn refresh(job: &mut Job) -> Result<(), ToolError> {
    if job.state == JobState::Running {
        if let Some(code) = job.child.try_wait().map_err(map_sandbox_error)? {
            job.state = JobState::Exited(code);
        }
    }
    Ok(())
}

fn render_job(id: &str, job: &Job) -> String {
    let output = job.output.lock().unwrap_or_else(|error| error.into_inner());
    json!({
        "job_id": id,
        "state": job.state.label(),
        "exit_code": job.state.exit_code(),
        "output": String::from_utf8_lossy(&output),
    })
    .to_string()
}

fn drain(mut reader: Box<dyn Read + Send>, output: Arc<Mutex<Vec<u8>>>) {
    let mut chunk = [0u8; 8192];
    loop {
        match reader.read(&mut chunk) {
            Ok(0) => break,
            Ok(count) => {
                let mut buffer = output.lock().unwrap_or_else(|error| error.into_inner());
                // Keep the newest output so `status` can report the tail.
                if count >= JOB_OUTPUT_LIMIT {
                    buffer.clear();
                    buffer.extend_from_slice(&chunk[count - JOB_OUTPUT_LIMIT..count]);
                } else {
                    let excess = buffer
                        .len()
                        .saturating_sub(JOB_OUTPUT_LIMIT.saturating_sub(count));
                    if excess > 0 {
                        buffer.drain(..excess);
                    }
                    buffer.extend_from_slice(&chunk[..count]);
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => break,
        }
    }
}

fn parse_action(args: &Value) -> Result<JobAction, ToolError> {
    JobAction::parse(required(args, "action")?)
}

fn required<'a>(args: &'a Value, name: &str) -> Result<&'a str, ToolError> {
    match args.get(name) {
        Some(Value::String(value)) if !value.trim().is_empty() => Ok(value),
        Some(Value::String(_)) => Err(ToolError::InvalidArgs(format!("`{name}` is empty"))),
        Some(_) => Err(ToolError::InvalidArgs(format!("`{name}` must be a string"))),
        None => Err(ToolError::InvalidArgs(format!("`{name}` is required"))),
    }
}

fn optional<'a>(args: &'a Value, name: &str) -> Result<Option<&'a str>, ToolError> {
    match args.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) => Ok(Some(value)),
        Some(_) => Err(ToolError::InvalidArgs(format!("`{name}` must be a string"))),
    }
}

fn map_sandbox_error(error: kl_sandbox::SandboxError) -> ToolError {
    match error {
        kl_sandbox::SandboxError::Violation(violation) => ToolError::Sandbox(violation),
        other => ToolError::Execution(other.to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::{PolicyConfig, PolicyEngine};
    use kl_sandbox::SandboxManager;
    use std::sync::atomic::AtomicBool;
    use std::time::{Duration, Instant};

    fn context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
        ToolContext {
            policy,
            cancel,
            sandbox: Arc::new(SandboxManager::policy_only()),
            timeout: Duration::from_secs(10),
            max_output_bytes: 64 * 1024,
        }
    }

    fn shell_policy(workspace: &std::path::Path) -> PolicyEngine {
        PolicyEngine::new(
            workspace,
            PolicyConfig {
                auto_approve_shell: true,
                ..PolicyConfig::default()
            },
        )
        .unwrap()
    }

    #[test]
    fn start_then_status_reports_exit() {
        let temp = tempfile::tempdir().unwrap();
        let policy = shell_policy(temp.path());
        let cancel = AtomicBool::new(false);
        let context = context(&policy, &cancel);
        let tool = JobTool::new();
        let command = if cfg!(windows) {
            "echo job-hello"
        } else {
            "printf job-hello"
        };

        let start = tool
            .execute(&context, &json!({"action": "start", "command": command}))
            .unwrap();
        assert!(start.output.contains("job-1"), "{}", start.output);

        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let status = tool
                .execute(&context, &json!({"action": "status", "job_id": "job-1"}))
                .unwrap();
            if status.output.contains("\"state\":\"exited\"") && status.output.contains("job-hello")
            {
                assert!(
                    status.output.contains("\"exit_code\":0"),
                    "{}",
                    status.output
                );
                break;
            }
            assert!(
                Instant::now() < deadline,
                "job did not exit: {}",
                status.output
            );
            std::thread::sleep(Duration::from_millis(20));
        }
    }

    #[test]
    fn invalid_arguments_and_unknown_job_are_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let policy = shell_policy(temp.path());
        let cancel = AtomicBool::new(false);
        let context = context(&policy, &cancel);
        let tool = JobTool::new();

        let missing = tool
            .execute(&context, &json!({"action": "start"}))
            .unwrap_err();
        assert!(matches!(missing, ToolError::InvalidArgs(_)), "{missing}");

        let outside = tempfile::tempdir().unwrap();
        let escaped = tool
            .execute(
                &context,
                &json!({
                    "action": "start",
                    "command": "echo nope",
                    "cwd": outside.path().to_string_lossy()
                }),
            )
            .unwrap_err();
        assert!(matches!(escaped, ToolError::Policy(_)), "{escaped}");

        let unknown = tool
            .execute(&context, &json!({"action": "status", "job_id": "job-404"}))
            .unwrap_err();
        assert!(matches!(unknown, ToolError::NotFound(_)), "{unknown}");
    }

    #[test]
    fn stop_marks_running_job_stopped() {
        let temp = tempfile::tempdir().unwrap();
        let policy = shell_policy(temp.path());
        let cancel = AtomicBool::new(false);
        let context = context(&policy, &cancel);
        let tool = JobTool::new();
        #[cfg(windows)]
        let command = "ping 127.0.0.1 -n 30 > nul";
        #[cfg(not(windows))]
        let command = "sleep 30";

        tool.execute(&context, &json!({"action": "start", "command": command}))
            .unwrap();
        let stopped = tool
            .execute(&context, &json!({"action": "stop", "job_id": "job-1"}))
            .unwrap();
        assert!(
            stopped.output.contains("\"state\":\"stopped\""),
            "{}",
            stopped.output
        );

        let status = tool
            .execute(&context, &json!({"action": "status", "job_id": "job-1"}))
            .unwrap();
        assert!(
            status.output.contains("\"state\":\"stopped\""),
            "{}",
            status.output
        );
    }
}
