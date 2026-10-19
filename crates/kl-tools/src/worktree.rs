use crate::shell::{apply_env_policy, describe_output};
use crate::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, string_arg, truncate_text};
use kl_exec::CommandSpec;
use kl_policy::{Action, Effect};
use kl_sandbox::SandboxPolicy;
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

pub struct WorktreeTool;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum WorktreeAction {
    Create,
    List,
    Remove,
}

impl WorktreeAction {
    fn parse(value: &str) -> Result<Self, ToolError> {
        match value {
            "create" => Ok(Self::Create),
            "list" => Ok(Self::List),
            "remove" => Ok(Self::Remove),
            _ => Err(ToolError::InvalidArgs(
                "`action` must be create, list or remove".to_owned(),
            )),
        }
    }

    fn policy_action(self) -> Action {
        match self {
            Self::List => Action::GitRead,
            Self::Create | Self::Remove => Action::GitWrite,
        }
    }
}

impl Tool for WorktreeTool {
    fn name(&self) -> &'static str {
        "worktree"
    }

    fn description(&self) -> &'static str {
        "Create, list or remove isolated git worktrees under .kl/worktrees."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "action": {"type": "string", "enum": ["create", "list", "remove"]},
                "name": {"type": "string", "description": "Required for create and remove; [A-Za-z0-9._-]+"}
            },
            "required": ["action"],
            "additionalProperties": false
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        parse_args(args).map(|_| ())
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let (action, name) = parse_args(args)?;
        Ok(PolicyRequest {
            action: action.policy_action(),
            resource: match name {
                Some(name) => format!("worktree:.kl/worktrees/{name}"),
                None => "worktree:.kl/worktrees".to_owned(),
            },
        })
    }

    fn read_only(&self, args: &Value) -> bool {
        matches!(parse_args(args), Ok((WorktreeAction::List, _)))
    }

    fn destructive(&self, _args: &Value) -> bool {
        true
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let (action, name) = parse_args(args)?;
        let request = self.policy_request(args)?;
        let evaluation = context.policy.evaluate(request.action, &request.resource);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }

        match action {
            WorktreeAction::Create => create(context, required_name(name)?),
            WorktreeAction::List => list(context),
            WorktreeAction::Remove => remove(context, required_name(name)?),
        }
    }
}

fn parse_args(args: &Value) -> Result<(WorktreeAction, Option<&str>), ToolError> {
    let action = WorktreeAction::parse(string_arg(args, "action")?)?;
    let name = match args.get("name") {
        None | Some(Value::Null) => None,
        Some(Value::String(name)) => Some(name.as_str()),
        Some(_) => return Err(ToolError::InvalidArgs("`name` must be a string".to_owned())),
    };
    if let Some(name) = name {
        validate_name(name)?;
    }
    if action != WorktreeAction::List && name.is_none() {
        return Err(ToolError::InvalidArgs(
            "`name` is required for create and remove".to_owned(),
        ));
    }
    Ok((action, name))
}

fn required_name(name: Option<&str>) -> Result<&str, ToolError> {
    name.ok_or_else(|| ToolError::InvalidArgs("`name` is required".to_owned()))
}

fn validate_name(name: &str) -> Result<(), ToolError> {
    if name == "." || name == ".." || name.is_empty() {
        return Err(ToolError::InvalidArgs("invalid worktree name".to_owned()));
    }
    if !name
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(ToolError::InvalidArgs(
            "worktree name must match [A-Za-z0-9._-]+".to_owned(),
        ));
    }
    #[cfg(windows)]
    if name.ends_with('.') || is_reserved_windows_name(name) {
        return Err(ToolError::InvalidArgs(format!(
            "worktree name `{name}` is not valid on Windows"
        )));
    }
    Ok(())
}

#[cfg(windows)]
fn is_reserved_windows_name(name: &str) -> bool {
    let stem = name.split('.').next().unwrap_or(name).to_ascii_uppercase();
    matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
        || matches!(
            stem.as_bytes(),
            [b'C' | b'L', b'O' | b'P', b'M' | b'T', b'1'..=b'9']
        )
}

fn create(context: &ToolContext<'_>, name: &str) -> Result<ToolOutcome, ToolError> {
    let root = prepare_root(context)?;
    let path = root.join(name);
    match fs::symlink_metadata(&path) {
        Ok(_) => {
            return Err(ToolError::InvalidArgs(format!(
                "worktree `{name}` already exists"
            )));
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(ToolError::Io(error.to_string())),
    }

    let mut spec = git_spec(
        context,
        vec![
            "worktree".to_owned(),
            "add".to_owned(),
            "--detach".to_owned(),
            path.to_string_lossy().into_owned(),
            "HEAD".to_owned(),
        ],
    );
    spec.timeout = context.timeout;
    let output = run(context, &spec)?;
    let outcome = describe_output(output, context.max_output_bytes);
    if !outcome.success {
        return Ok(outcome);
    }
    if !path.is_dir() {
        return Err(ToolError::Execution(format!(
            "git reported success but {} does not exist",
            path.display()
        )));
    }
    Ok(outcome)
}

fn list(context: &ToolContext<'_>) -> Result<ToolOutcome, ToolError> {
    let root = workspace_root(context)?;
    if !root.exists() {
        return Ok(ToolOutcome::ok("0 worktrees", ""));
    }
    ensure_dir(&root)?;
    let mut spec = git_spec(
        context,
        vec![
            "worktree".to_owned(),
            "list".to_owned(),
            "--porcelain".to_owned(),
        ],
    );
    spec.timeout = context.timeout;
    let output = run(context, &spec)?;
    if !output.success() {
        return Ok(describe_output(output, context.max_output_bytes));
    }
    let entries = filter_worktrees(&output.combined(), &root);
    let summary = format!(
        "{} worktree{}",
        entries.len(),
        if entries.len() == 1 { "" } else { "s" }
    );
    Ok(ToolOutcome::ok(
        summary,
        truncate_text(&entries.join("\n\n"), context.max_output_bytes),
    ))
}

fn remove(context: &ToolContext<'_>, name: &str) -> Result<ToolOutcome, ToolError> {
    let root = workspace_root(context)?;
    if !root.exists() {
        return Err(ToolError::NotFound(format!("worktree `{name}`")));
    }
    ensure_dir(&root)?;
    let path = root.join(name);
    match fs::symlink_metadata(&path) {
        Ok(metadata) if metadata.file_type().is_symlink() => {
            return Err(ToolError::Policy(format!(
                "refusing to remove symlink {}",
                path.display()
            )));
        }
        Ok(_) => {}
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Err(ToolError::NotFound(format!("worktree `{name}`")));
        }
        Err(error) => return Err(ToolError::Io(error.to_string())),
    }

    let mut spec = git_spec(
        context,
        vec![
            "worktree".to_owned(),
            "remove".to_owned(),
            "--force".to_owned(),
            path.to_string_lossy().into_owned(),
        ],
    );
    spec.timeout = context.timeout;
    let output = run(context, &spec)?;
    let outcome = describe_output(output, context.max_output_bytes);
    if outcome.success && fs::symlink_metadata(&path).is_ok() {
        return Err(ToolError::Execution(format!(
            "git reported success but {} still exists",
            path.display()
        )));
    }
    Ok(outcome)
}

fn prepare_root(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let kl = context.policy.workspace().join(".kl");
    ensure_dir(&kl)?;
    let root = kl.join("worktrees");
    ensure_dir(&root)?;
    resolved_root(context)
}

fn workspace_root(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let kl = context.policy.workspace().join(".kl");
    if fs::symlink_metadata(&kl).is_ok() {
        ensure_dir(&kl)?;
    }
    let root = kl.join("worktrees");
    if root.exists() {
        ensure_dir(&root)?;
        return resolved_root(context);
    }
    Ok(root)
}

fn resolved_root(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let root = context
        .policy
        .resolve(".kl/worktrees")
        .map_err(|error| ToolError::Policy(error.to_string()))?;
    if !root.starts_with(context.policy.workspace()) {
        return Err(ToolError::Policy(format!(
            "worktree root escapes workspace: {}",
            root.display()
        )));
    }
    Ok(root)
}

fn ensure_dir(path: &Path) -> Result<(), ToolError> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() {
                return Err(ToolError::Policy(format!(
                    "refusing to use symlinked worktree root {}",
                    path.display()
                )));
            }
            if !metadata.is_dir() {
                return Err(ToolError::Io(format!(
                    "{} is not a directory",
                    path.display()
                )));
            }
            Ok(())
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => fs::create_dir(path)
            .map_err(|error| ToolError::Io(format!("cannot create {}: {error}", path.display()))),
        Err(error) => Err(ToolError::Io(error.to_string())),
    }
}

fn git_spec(context: &ToolContext<'_>, args: Vec<String>) -> CommandSpec {
    let mut spec = CommandSpec::new("git", args, context.policy.workspace());
    spec.max_output_bytes = context.max_output_bytes;
    apply_env_policy(context.policy, &mut spec);
    spec
}

fn run(context: &ToolContext<'_>, spec: &CommandSpec) -> Result<kl_exec::CommandOutput, ToolError> {
    let policy = SandboxPolicy::workspace(context.policy.workspace());
    context
        .sandbox
        .run(spec, context.cancel, &policy)
        .map_err(|error| match error {
            kl_sandbox::SandboxError::Violation(violation) => ToolError::Sandbox(violation),
            other => ToolError::Policy(other.to_string()),
        })
}

fn filter_worktrees(porcelain: &str, root: &Path) -> Vec<String> {
    let mut entries: Vec<String> = Vec::new();
    let mut current: Vec<String> = Vec::new();
    for line in porcelain.lines().chain(std::iter::once("")) {
        if line.trim().is_empty() {
            if !current.is_empty() {
                let include = current
                    .first()
                    .and_then(|line| line.strip_prefix("worktree "))
                    .is_some_and(|path| Path::new(path).starts_with(root));
                if include {
                    entries.push(current.join("\n"));
                }
                current.clear();
            }
        } else {
            current.push(line.to_owned());
        }
    }
    entries
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::{PolicyConfig, PolicyEngine};
    use kl_sandbox::SandboxManager;
    use std::process::Command;
    use std::sync::Arc;
    use std::sync::atomic::AtomicBool;
    use std::time::Duration;

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
    fn rejects_invalid_names() {
        for name in ["", ".", "..", "../escape", "a/b", "a\\b", "a b", "a:b"] {
            let error = WorktreeTool
                .validate(&json!({"action": "create", "name": name}))
                .unwrap_err();
            assert!(matches!(error, ToolError::InvalidArgs(_)), "{name:?}");
        }
        #[cfg(windows)]
        for name in ["...", "CON", "con.txt", "LPT9"] {
            let error = WorktreeTool
                .validate(&json!({"action": "create", "name": name}))
                .unwrap_err();
            assert!(matches!(error, ToolError::InvalidArgs(_)), "{name:?}");
        }
        assert!(WorktreeTool.validate(&json!({"action": "create"})).is_err());
        assert!(WorktreeTool.validate(&json!({"action": "list"})).is_ok());
        assert_eq!(
            WorktreeTool
                .policy_request(&json!({"action": "create", "name": "feature"}))
                .unwrap()
                .action,
            Action::GitWrite
        );
        assert_eq!(
            WorktreeTool
                .policy_request(&json!({"action": "list"}))
                .unwrap()
                .action,
            Action::GitRead
        );
    }

    #[test]
    fn create_list_remove_round_trip() {
        if Command::new("git").arg("--version").output().is_err() {
            return;
        }
        let temp = tempfile::tempdir().unwrap();
        let workspace = temp.path();
        fs::write(workspace.join("README.md"), "test\n").unwrap();
        let init = Command::new("git")
            .args(["init", "-q"])
            .current_dir(workspace)
            .status()
            .unwrap();
        assert!(init.success());
        let commit = Command::new("git")
            .args([
                "-c",
                "user.name=KL Test",
                "-c",
                "user.email=kl@example.invalid",
                "add",
                ".",
            ])
            .current_dir(workspace)
            .status()
            .unwrap();
        assert!(commit.success());
        let commit = Command::new("git")
            .args([
                "-c",
                "user.name=KL Test",
                "-c",
                "user.email=kl@example.invalid",
                "commit",
                "-qm",
                "init",
            ])
            .current_dir(workspace)
            .status()
            .unwrap();
        assert!(commit.success());

        let policy = PolicyEngine::new(workspace, PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let context = context(&policy, &cancel);
        let created = WorktreeTool
            .execute(&context, &json!({"action": "create", "name": "feature"}))
            .unwrap();
        assert!(created.success, "{}", created.output);
        let path = workspace.join(".kl/worktrees/feature");
        assert!(path.is_dir());

        let listed = WorktreeTool
            .execute(&context, &json!({"action": "list"}))
            .unwrap();
        assert!(listed.success, "{}", listed.output);
        assert!(listed.output.contains("feature"), "{}", listed.output);
        assert_eq!(listed.output.matches("worktree ").count(), 1);

        let removed = WorktreeTool
            .execute(&context, &json!({"action": "remove", "name": "feature"}))
            .unwrap();
        assert!(removed.success, "{}", removed.output);
        assert!(!path.exists());
    }
}
