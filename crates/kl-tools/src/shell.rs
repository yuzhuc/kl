use crate::{
    PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, optional_string, optional_usize,
    string_arg, truncate_text,
};
use kl_exec::{CommandOutput, CommandSpec};
use kl_policy::{Action, Effect, is_read_only_git};
use kl_sandbox::SandboxPolicy;
use serde_json::{Value, json};
use std::path::PathBuf;
use std::time::Duration;

pub struct ShellTool;

impl Tool for ShellTool {
    fn name(&self) -> &'static str {
        "shell"
    }

    fn destructive(&self, _args: &Value) -> bool {
        true
    }

    fn description(&self) -> &'static str {
        "Run a shell command in the workspace. Use for builds, tests and other CLI tools."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "command": {"type": "string"},
                "cwd": {"type": "string", "default": "."},
                "timeout_ms": {"type": "integer", "minimum": 100}
            },
            "required": ["command"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Shell,
            resource: string_arg(args, "command")?.to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let command = string_arg(args, "command")?;
        let evaluation = context.policy.evaluate(Action::Shell, command);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let cwd = resolve_cwd(context, optional_string(args, "cwd").unwrap_or("."))?;
        let timeout = optional_usize(args, "timeout_ms")
            .map(|millis| Duration::from_millis(millis as u64))
            .unwrap_or(context.timeout)
            .min(context.timeout)
            .max(Duration::from_millis(100));
        let mut spec = if cfg!(windows) {
            CommandSpec::new("cmd", vec!["/C".to_owned(), command.to_owned()], cwd)
        } else {
            CommandSpec::new("sh", vec!["-c".to_owned(), command.to_owned()], cwd)
        };
        spec.timeout = timeout;
        spec.max_output_bytes = context.max_output_bytes;
        apply_env_policy(context.policy, &mut spec);
        let policy = SandboxPolicy::workspace(context.policy.workspace());
        let output = context
            .sandbox
            .run(&spec, context.cancel, &policy)
            .map_err(|error| match error {
                kl_sandbox::SandboxError::Violation(violation) => ToolError::Sandbox(violation),
                other => ToolError::Policy(other.to_string()),
            })?;
        Ok(describe_output(output, context.max_output_bytes))
    }
}

pub struct GitTool;

impl Tool for GitTool {
    fn name(&self) -> &'static str {
        "git"
    }

    fn destructive(&self, _args: &Value) -> bool {
        true
    }

    fn description(&self) -> &'static str {
        "Run git with an argument array. Read-only commands are allowed; mutations require approval."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "args": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "Arguments passed directly to git, e.g. [\"status\", \"--short\"]"
                },
                "cwd": {"type": "string", "default": "."}
            },
            "required": ["args"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let git_args = git_args(args)?;
        let joined = git_args.join(" ");
        Ok(PolicyRequest {
            action: if is_read_only_git(&joined) {
                Action::GitRead
            } else {
                Action::GitWrite
            },
            resource: joined,
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let git_args = git_args(args)?;
        let joined = git_args.join(" ");
        let action = if is_read_only_git(&joined) {
            Action::GitRead
        } else {
            Action::GitWrite
        };
        let evaluation = context.policy.evaluate(action, &joined);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let cwd = resolve_cwd(context, optional_string(args, "cwd").unwrap_or("."))?;
        let mut spec = CommandSpec::new("git", git_args, cwd);
        spec.timeout = context.timeout;
        spec.max_output_bytes = context.max_output_bytes;
        apply_env_policy(context.policy, &mut spec);
        let policy = SandboxPolicy::workspace(context.policy.workspace());
        let output = context
            .sandbox
            .run(&spec, context.cancel, &policy)
            .map_err(|error| match error {
                kl_sandbox::SandboxError::Violation(violation) => ToolError::Sandbox(violation),
                other => ToolError::Policy(other.to_string()),
            })?;
        Ok(describe_output(output, context.max_output_bytes))
    }
}

fn git_args(args: &Value) -> Result<Vec<String>, ToolError> {
    let value = args
        .get("args")
        .ok_or_else(|| ToolError::InvalidArgs("missing `args`".to_owned()))?;
    if let Some(array) = value.as_array() {
        let mut parsed = Vec::new();
        for item in array {
            parsed.push(
                item.as_str()
                    .ok_or_else(|| {
                        ToolError::InvalidArgs("all git args must be strings".to_owned())
                    })?
                    .to_owned(),
            );
        }
        if parsed.is_empty() {
            return Err(ToolError::InvalidArgs(
                "`args` must not be empty".to_owned(),
            ));
        }
        Ok(parsed)
    } else if let Some(text) = value.as_str() {
        let parsed: Vec<String> = text.split_whitespace().map(str::to_owned).collect();
        if parsed.is_empty() {
            return Err(ToolError::InvalidArgs(
                "`args` must not be empty".to_owned(),
            ));
        }
        Ok(parsed)
    } else {
        Err(ToolError::InvalidArgs(
            "`args` must be an array of strings".to_owned(),
        ))
    }
}

fn apply_env_policy(policy: &kl_policy::PolicyEngine, spec: &mut CommandSpec) {
    let allowed = &policy.config().env_allowlist;
    if allowed.is_empty() {
        return;
    }
    spec.clear_env = true;
    let mut names: Vec<String> = if cfg!(windows) {
        [
            "PATH",
            "PATHEXT",
            "SystemRoot",
            "WINDIR",
            "ComSpec",
            "TEMP",
            "TMP",
            "USERPROFILE",
        ]
        .into_iter()
        .map(str::to_owned)
        .collect()
    } else {
        ["PATH", "HOME", "TMPDIR", "LANG", "LC_ALL"]
            .into_iter()
            .map(str::to_owned)
            .collect()
    };
    names.extend(allowed.iter().cloned());
    names.sort();
    names.dedup();
    for name in names {
        if let Ok(value) = std::env::var(&name) {
            spec.env.push((name, value));
        }
    }
}

fn resolve_cwd(context: &ToolContext<'_>, raw: &str) -> Result<PathBuf, ToolError> {
    let evaluation = context.policy.evaluate(Action::Read, raw);
    if evaluation.effect == Effect::Deny {
        return Err(ToolError::Policy(evaluation.reason));
    }
    evaluation
        .resolved
        .ok_or_else(|| ToolError::Io("cwd was not resolved".to_owned()))
}

fn describe_output(output: CommandOutput, limit: usize) -> ToolOutcome {
    let summary = if output.timed_out {
        format!("timed out after {} ms", output.duration_ms)
    } else if output.cancelled {
        "cancelled".to_owned()
    } else {
        format!(
            "exit {} in {} ms",
            output
                .status
                .map(|code| code.to_string())
                .unwrap_or_else(|| "signal".to_owned()),
            output.duration_ms
        )
    };
    let mut text = output.combined();
    if text.is_empty() {
        text = "(no output)".to_owned();
    }
    if output.truncated {
        text.push_str("\n... [tool output truncated]");
    }
    ToolOutcome {
        success: output.success(),
        summary,
        output: truncate_text(&text, limit),
        diff: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::{PolicyConfig, PolicyEngine};
    use kl_sandbox::{SandboxBackendKind, SandboxConfig, SandboxManager, SandboxMode};
    use std::sync::Arc;

    #[cfg(windows)]
    #[test]
    fn shell_runs_through_appcontainer_sandbox() {
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path();
        let manager = SandboxManager::probe(&SandboxConfig {
            mode: SandboxMode::Strict,
            backend: SandboxBackendKind::AppContainer,
            ..SandboxConfig::default()
        });
        if !manager.status().available {
            return;
        }
        let policy = PolicyEngine::new(workspace, PolicyConfig::default()).unwrap();
        let cancel = std::sync::atomic::AtomicBool::new(false);
        let context = ToolContext {
            policy: &policy,
            cancel: &cancel,
            sandbox: Arc::new(manager),
            timeout: Duration::from_secs(20),
            max_output_bytes: 64 * 1024,
        };
        let outcome = ShellTool
            .execute(&context, &json!({"command": "echo hello>a.txt"}))
            .unwrap();
        assert!(outcome.success, "{}", outcome.output);
        assert_eq!(
            std::fs::read_to_string(workspace.join("a.txt"))
                .unwrap()
                .trim(),
            "hello"
        );
    }
}
