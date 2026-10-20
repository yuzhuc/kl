use crate::{
    PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, diff, string_arg, truncate_text,
};
use kl_policy::{Action, Effect};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

const MEMORY_DIR: &str = ".kl/memory";
const MAX_ENTRY_BYTES: usize = 64 * 1024;
const MAX_ENTRIES: usize = 100;

pub struct MemoryTool;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum MemoryAction {
    List,
    Read,
    Write,
    Delete,
}

impl MemoryAction {
    fn parse(value: &str) -> Result<Self, ToolError> {
        match value {
            "list" => Ok(Self::List),
            "read" => Ok(Self::Read),
            "write" => Ok(Self::Write),
            "delete" => Ok(Self::Delete),
            other => Err(ToolError::InvalidArgs(format!(
                "`action` must be list, read, write or delete (got {other})"
            ))),
        }
    }

    fn read_only(self) -> bool {
        matches!(self, Self::List | Self::Read)
    }
}

impl Tool for MemoryTool {
    fn name(&self) -> &'static str {
        "memory"
    }

    fn description(&self) -> &'static str {
        "Store and recall workspace notes as Markdown files under .kl/memory."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["list", "read", "write", "delete"]
                },
                "name": {
                    "type": "string",
                    "description": "Entry name such as notes.md; required for read, write and delete"
                },
                "content": {
                    "type": "string",
                    "description": "Markdown content; required for write"
                }
            },
            "required": ["action"],
            "additionalProperties": false
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        match parse_action(args)? {
            MemoryAction::List => {}
            MemoryAction::Read | MemoryAction::Delete => {
                required_name(args)?;
            }
            MemoryAction::Write => {
                required_name(args)?;
                string_arg(args, "content")?;
            }
        }
        Ok(())
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let action = parse_action(args)?;
        Ok(PolicyRequest {
            action: match action {
                MemoryAction::Write | MemoryAction::Delete => Action::Edit,
                MemoryAction::List | MemoryAction::Read => Action::Read,
            },
            resource: match action {
                MemoryAction::List => MEMORY_DIR.to_owned(),
                _ => format!("{MEMORY_DIR}/{}", required_name(args)?),
            },
        })
    }

    fn read_only(&self, args: &Value) -> bool {
        parse_action(args).is_ok_and(MemoryAction::read_only)
    }

    fn destructive(&self, args: &Value) -> bool {
        !self.read_only(args)
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let action = parse_action(args)?;
        let request = self.policy_request(args)?;
        let evaluation = context.policy.evaluate(request.action, &request.resource);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }

        match action {
            MemoryAction::List => list(context),
            MemoryAction::Read => read(context, required_name(args)?),
            MemoryAction::Write => {
                write(context, required_name(args)?, string_arg(args, "content")?)
            }
            MemoryAction::Delete => delete(context, required_name(args)?),
        }
    }
}

fn parse_action(args: &Value) -> Result<MemoryAction, ToolError> {
    MemoryAction::parse(string_arg(args, "action")?)
}

fn required_name(args: &Value) -> Result<&str, ToolError> {
    let name = string_arg(args, "name")?;
    validate_name(name)?;
    Ok(name)
}

fn validate_name(name: &str) -> Result<(), ToolError> {
    let stem = name.strip_suffix(".md").unwrap_or_default();
    let valid = !stem.is_empty()
        && stem.starts_with(|character: char| character.is_ascii_alphanumeric())
        && stem.chars().all(|character| {
            character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | '-')
        })
        && !is_reserved_windows_name(stem.split('.').next().unwrap_or(stem));
    if !valid {
        return Err(ToolError::InvalidArgs(format!(
            "memory entry name `{name}` must be <name>.md using [A-Za-z0-9._-]"
        )));
    }
    Ok(())
}

fn is_reserved_windows_name(name: &str) -> bool {
    #[cfg(windows)]
    {
        let upper = name.to_ascii_uppercase();
        matches!(upper.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || matches!(
                upper.as_bytes(),
                [b'C' | b'L', b'O' | b'P', b'M' | b'T', b'1'..=b'9']
            )
    }
    #[cfg(not(windows))]
    {
        let _ = name;
        false
    }
}

fn list(context: &ToolContext<'_>) -> Result<ToolOutcome, ToolError> {
    let root = memory_root(context)?;
    let mut names = Vec::new();
    if let Ok(entries) = fs::read_dir(&root) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().into_owned();
            if name.ends_with(".md") && entry.file_type().is_ok_and(|kind| kind.is_file()) {
                names.push(name);
            }
        }
    }
    names.sort();
    let summary = format!(
        "{} memory entr{}",
        names.len(),
        if names.len() == 1 { "y" } else { "ies" }
    );
    Ok(ToolOutcome::ok(
        summary,
        truncate_text(&names.join("\n"), context.max_output_bytes),
    ))
}

fn read(context: &ToolContext<'_>, name: &str) -> Result<ToolOutcome, ToolError> {
    let root = memory_root(context)?;
    let path = entry_path(context, &root, name)?;
    let metadata = fs::metadata(&path).map_err(|error| match error.kind() {
        std::io::ErrorKind::NotFound => ToolError::NotFound(format!("memory entry `{name}`")),
        _ => ToolError::Io(format!("cannot read {}: {error}", path.display())),
    })?;
    if metadata.len() > MAX_ENTRY_BYTES as u64 {
        return Err(ToolError::InvalidArgs(format!(
            "memory entry `{name}` exceeds the {MAX_ENTRY_BYTES} byte limit"
        )));
    }
    let content = fs::read_to_string(&path)
        .map_err(|error| ToolError::Io(format!("cannot read {}: {error}", path.display())))?;
    Ok(ToolOutcome::ok(
        format!("read {name}"),
        truncate_text(&content, context.max_output_bytes),
    ))
}

fn write(context: &ToolContext<'_>, name: &str, content: &str) -> Result<ToolOutcome, ToolError> {
    if content.len() > MAX_ENTRY_BYTES {
        return Err(ToolError::InvalidArgs(format!(
            "memory entry `{name}` exceeds the {MAX_ENTRY_BYTES} byte limit"
        )));
    }
    let root = ensure_root(context)?;
    let path = entry_path(context, &root, name)?;
    let existed = path.exists();
    let old = fs::read(&path)
        .map(|bytes| String::from_utf8_lossy(&bytes).into_owned())
        .unwrap_or_default();
    if existed && old == content {
        return Ok(ToolOutcome::ok(format!("{name} unchanged"), ""));
    }
    if !existed && count_entries(&root) >= MAX_ENTRIES {
        return Err(ToolError::InvalidArgs(format!(
            "memory store is full ({MAX_ENTRIES} entries maximum)"
        )));
    }
    fs::write(&path, content)
        .map_err(|error| ToolError::Io(format!("cannot write {}: {error}", path.display())))?;
    let summary = format!("wrote {name} ({} bytes)", content.len());
    Ok(
        ToolOutcome::ok(summary.clone(), summary).with_diff(Some(diff::unified_diff(
            &format!("{MEMORY_DIR}/{name}"),
            &old,
            content,
        ))),
    )
}

fn delete(context: &ToolContext<'_>, name: &str) -> Result<ToolOutcome, ToolError> {
    let root = memory_root(context)?;
    let path = entry_path(context, &root, name)?;
    fs::remove_file(&path).map_err(|error| match error.kind() {
        std::io::ErrorKind::NotFound => ToolError::NotFound(format!("memory entry `{name}`")),
        _ => ToolError::Io(format!("cannot delete {}: {error}", path.display())),
    })?;
    Ok(ToolOutcome::ok(
        format!("deleted {name}"),
        format!("deleted {name}"),
    ))
}

fn count_entries(root: &Path) -> usize {
    fs::read_dir(root)
        .map(|entries| {
            entries
                .flatten()
                .filter(|entry| entry.file_name().to_string_lossy().ends_with(".md"))
                .filter(|entry| entry.file_type().is_ok_and(|kind| kind.is_file()))
                .count()
        })
        .unwrap_or(0)
}

/// Root of the memory store, resolved through the policy engine so a
/// symlinked `.kl` directory cannot redirect the store outside the workspace.
fn memory_root(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let root = context
        .policy
        .resolve(MEMORY_DIR)
        .map_err(|error| ToolError::Policy(error.to_string()))?;
    if !root.starts_with(context.policy.workspace()) {
        return Err(ToolError::Policy(format!(
            "memory directory escapes the workspace: {}",
            root.display()
        )));
    }
    Ok(root)
}

fn ensure_root(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let root = memory_root(context)?;
    if !root.exists() {
        fs::create_dir_all(&root)
            .map_err(|error| ToolError::Io(format!("cannot create {}: {error}", root.display())))?;
    }
    memory_root(context)
}

/// Path of a single entry. The name is validated by `validate_name`, and the
/// resolved path is checked against the memory root so neither `..` nor a
/// symlinked entry can reach outside the store.
fn entry_path(context: &ToolContext<'_>, root: &Path, name: &str) -> Result<PathBuf, ToolError> {
    if fs::symlink_metadata(root.join(name)).is_ok_and(|metadata| metadata.file_type().is_symlink())
    {
        return Err(ToolError::Policy(format!(
            "refusing to use symlinked memory entry {name}"
        )));
    }
    let resolved = context
        .policy
        .resolve(&format!("{MEMORY_DIR}/{name}"))
        .map_err(|error| ToolError::Policy(error.to_string()))?;
    if !resolved.starts_with(root) {
        return Err(ToolError::Policy(format!(
            "memory entry `{name}` escapes the memory directory"
        )));
    }
    Ok(resolved)
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::{PolicyConfig, PolicyEngine};
    use kl_sandbox::SandboxManager;
    use std::sync::Arc;
    use std::sync::atomic::AtomicBool;
    use std::time::Duration;

    fn context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
        ToolContext {
            policy,
            cancel,
            sandbox: Arc::new(SandboxManager::policy_only()),
            timeout: Duration::from_secs(10),
            max_output_bytes: 64 * 1024,
        }
    }

    fn setup() -> (tempfile::TempDir, PolicyEngine, AtomicBool) {
        let temp = tempfile::tempdir().unwrap();
        let policy = PolicyEngine::new(
            temp.path(),
            PolicyConfig {
                auto_approve_edits: true,
                ..PolicyConfig::default()
            },
        )
        .unwrap();
        (temp, policy, AtomicBool::new(false))
    }

    #[test]
    fn write_read_list_delete_round_trip() {
        let (temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = MemoryTool;

        let written = tool
            .execute(
                &context,
                &json!({"action": "write", "name": "notes.md", "content": "# hello"}),
            )
            .unwrap();
        assert!(written.success, "{}", written.output);
        assert!(temp.path().join(".kl/memory/notes.md").is_file());

        let read = tool
            .execute(&context, &json!({"action": "read", "name": "notes.md"}))
            .unwrap();
        assert!(read.output.contains("# hello"), "{}", read.output);

        let listed = tool.execute(&context, &json!({"action": "list"})).unwrap();
        assert!(listed.output.contains("notes.md"), "{}", listed.output);

        let deleted = tool
            .execute(&context, &json!({"action": "delete", "name": "notes.md"}))
            .unwrap();
        assert!(deleted.success, "{}", deleted.output);
        assert!(!temp.path().join(".kl/memory/notes.md").exists());

        let listed = tool.execute(&context, &json!({"action": "list"})).unwrap();
        assert!(!listed.output.contains("notes.md"), "{}", listed.output);
    }

    #[test]
    fn traversal_names_are_rejected() {
        let (temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = MemoryTool;

        for name in [
            "../escape.md",
            "sub/dir.md",
            "..\\escape.md",
            "/absolute.md",
            "C:\\escape.md",
            "..",
            "notes.txt",
        ] {
            let error = tool
                .execute(
                    &context,
                    &json!({"action": "write", "name": name, "content": "x"}),
                )
                .unwrap_err();
            assert!(
                matches!(error, ToolError::InvalidArgs(_)),
                "{name:?}: {error}"
            );
        }
        assert!(!temp.path().join(".kl/escape.md").exists());
        assert!(!temp.path().parent().unwrap().join("escape.md").exists());
    }

    #[test]
    fn oversized_entries_are_rejected() {
        let (_temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = MemoryTool;

        let error = tool
            .execute(
                &context,
                &json!({
                    "action": "write",
                    "name": "big.md",
                    "content": "a".repeat(MAX_ENTRY_BYTES + 1)
                }),
            )
            .unwrap_err();
        assert!(matches!(error, ToolError::InvalidArgs(_)), "{error}");
    }

    #[test]
    fn entry_count_is_capped() {
        let (_temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = MemoryTool;

        for index in 0..MAX_ENTRIES {
            tool.execute(
                &context,
                &json!({
                    "action": "write",
                    "name": format!("note{index}.md"),
                    "content": "x"
                }),
            )
            .unwrap();
        }
        let error = tool
            .execute(
                &context,
                &json!({"action": "write", "name": "overflow.md", "content": "x"}),
            )
            .unwrap_err();
        assert!(matches!(error, ToolError::InvalidArgs(_)), "{error}");
    }

    #[test]
    fn policy_actions_follow_the_action() {
        let tool = MemoryTool;
        assert_eq!(
            tool.policy_request(&json!({"action": "write", "name": "a.md"}))
                .unwrap()
                .action,
            Action::Edit
        );
        for action in ["list", "read"] {
            let args = json!({"action": action, "name": "a.md"});
            assert_eq!(
                tool.policy_request(&args).unwrap().action,
                Action::Read,
                "{action}"
            );
        }
        assert_eq!(
            tool.policy_request(&json!({"action": "delete", "name": "a.md"}))
                .unwrap()
                .action,
            Action::Edit
        );
        for action in ["list", "read"] {
            let args = json!({"action": action, "name": "a.md"});
            assert!(tool.read_only(&args), "{action}");
            assert!(tool.concurrency_safe(&args), "{action}");
            assert!(!tool.destructive(&args), "{action}");
        }
        for args in [
            json!({"action": "write", "name": "a.md", "content": "x"}),
            json!({"action": "delete", "name": "a.md"}),
        ] {
            assert!(!tool.read_only(&args), "{args}");
            assert!(!tool.concurrency_safe(&args), "{args}");
            assert!(tool.destructive(&args), "{args}");
        }
    }
}
