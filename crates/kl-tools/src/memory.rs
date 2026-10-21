use crate::{
    PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, diff, string_arg, truncate_text,
};
use kl_policy::{Action, Effect};
use serde_json::{Value, json};
use std::ffi::OsString;
use std::fs;
use std::path::{Path, PathBuf};

const MEMORY_DIR: &str = ".kl/memory";
const MAX_ENTRY_BYTES: usize = 64 * 1024;
const MAX_ENTRIES: usize = 100;

/// Upper bounds for the memory block injected into the system prompt.
const MAX_INJECTED_ENTRIES: usize = 20;
const MAX_INJECTED_BYTES: usize = 8 * 1024;
const MAX_INJECTED_ENTRY_BYTES: usize = 2 * 1024;

/// Where a memory entry lives. Workspace entries shadow user entries with the
/// same name.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum MemoryScope {
    Workspace,
    User,
}

impl MemoryScope {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Workspace => "workspace",
            Self::User => "user",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "workspace" => Some(Self::Workspace),
            "user" => Some(Self::User),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MemoryEntry {
    pub name: String,
    pub scope: MemoryScope,
    pub bytes: u64,
}

/// The memory block that was injected into a turn.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MemoryInjection {
    pub text: String,
    pub entries: Vec<String>,
    pub truncated: bool,
}

/// Validated access to the Markdown memory store.
///
/// The workspace store lives in `<workspace>/.kl/memory`, the user store in
/// `<home>/memory`. Every path is resolved through `resolve_under`, so a
/// symlinked directory cannot redirect the store outside its base, and entry
/// names are restricted to a safe subset.
pub struct MemoryStore {
    workspace_root: PathBuf,
    user_root: Option<PathBuf>,
}

impl MemoryStore {
    /// Builds a store from already resolved roots.
    pub fn new(workspace_root: PathBuf, user_root: Option<PathBuf>) -> Self {
        Self {
            workspace_root,
            user_root,
        }
    }

    /// Resolves the workspace and user roots and verifies they stay inside
    /// their base directory.
    pub fn resolve(workspace: &Path, home: Option<&Path>) -> Result<Self, ToolError> {
        let workspace_root = resolve_under(workspace, MEMORY_DIR)?;
        let user_root = match home {
            Some(home) => Some(resolve_under(home, "memory")?),
            None => None,
        };
        Ok(Self::new(workspace_root, user_root))
    }

    fn root(&self, scope: MemoryScope) -> Result<&Path, ToolError> {
        match scope {
            MemoryScope::Workspace => Ok(&self.workspace_root),
            MemoryScope::User => self.user_root.as_deref().ok_or_else(|| {
                ToolError::InvalidArgs("the user memory scope is not available".to_owned())
            }),
        }
    }

    /// Lists the entries of one scope, or of both scopes when `scope` is
    /// `None`.
    pub fn list(&self, scope: Option<MemoryScope>) -> Vec<MemoryEntry> {
        let mut entries = Vec::new();
        for current in [MemoryScope::Workspace, MemoryScope::User] {
            if scope.is_some_and(|wanted| wanted != current) {
                continue;
            }
            let Ok(root) = self.root(current) else {
                continue;
            };
            let Ok(directory) = fs::read_dir(root) else {
                continue;
            };
            for entry in directory.flatten() {
                let name = entry.file_name().to_string_lossy().into_owned();
                if !name.ends_with(".md") {
                    continue;
                }
                let Ok(metadata) = entry.metadata() else {
                    continue;
                };
                if !metadata.is_file() {
                    continue;
                }
                entries.push(MemoryEntry {
                    name,
                    scope: current,
                    bytes: metadata.len(),
                });
            }
        }
        entries.sort_by(|left, right| {
            left.scope
                .cmp(&right.scope)
                .then_with(|| left.name.cmp(&right.name))
        });
        entries
    }

    pub fn read(&self, scope: MemoryScope, name: &str) -> Result<String, ToolError> {
        let path = self.entry_path(scope, name)?;
        let metadata = fs::metadata(&path).map_err(|error| match error.kind() {
            std::io::ErrorKind::NotFound => ToolError::NotFound(format!("memory entry `{name}`")),
            _ => ToolError::Io(format!("cannot read {}: {error}", path.display())),
        })?;
        if metadata.len() > MAX_ENTRY_BYTES as u64 {
            return Err(ToolError::InvalidArgs(format!(
                "memory entry `{name}` exceeds the {MAX_ENTRY_BYTES} byte limit"
            )));
        }
        fs::read_to_string(&path)
            .map_err(|error| ToolError::Io(format!("cannot read {}: {error}", path.display())))
    }

    /// Writes an entry and returns the previous content so callers can render
    /// a diff.
    pub fn write(
        &self,
        scope: MemoryScope,
        name: &str,
        content: &str,
    ) -> Result<(String, PathBuf), ToolError> {
        if content.len() > MAX_ENTRY_BYTES {
            return Err(ToolError::InvalidArgs(format!(
                "memory entry `{name}` exceeds the {MAX_ENTRY_BYTES} byte limit"
            )));
        }
        let root = self.root(scope)?.to_path_buf();
        let path = self.entry_path(scope, name)?;
        if !root.exists() {
            fs::create_dir_all(&root).map_err(|error| {
                ToolError::Io(format!("cannot create {}: {error}", root.display()))
            })?;
        }
        let old = fs::read(&path)
            .map(|bytes| String::from_utf8_lossy(&bytes).into_owned())
            .unwrap_or_default();
        if path.exists() && old == content {
            return Ok((old, path));
        }
        if !path.exists() && count_entries(&root) >= MAX_ENTRIES {
            return Err(ToolError::InvalidArgs(format!(
                "memory store is full ({MAX_ENTRIES} entries maximum)"
            )));
        }
        fs::write(&path, content)
            .map_err(|error| ToolError::Io(format!("cannot write {}: {error}", path.display())))?;
        Ok((old, path))
    }

    pub fn delete(&self, scope: MemoryScope, name: &str) -> Result<(), ToolError> {
        let path = self.entry_path(scope, name)?;
        fs::remove_file(&path).map_err(|error| match error.kind() {
            std::io::ErrorKind::NotFound => ToolError::NotFound(format!("memory entry `{name}`")),
            _ => ToolError::Io(format!("cannot delete {}: {error}", path.display())),
        })
    }

    /// Builds the bounded memory block for the system prompt. Workspace
    /// entries come first and shadow user entries with the same name.
    pub fn injection(&self) -> MemoryInjection {
        let mut injection = MemoryInjection::default();
        let mut entries: Vec<(String, MemoryScope)> = Vec::new();
        for entry in self.list(None) {
            if entries.iter().any(|(name, _)| *name == entry.name) {
                continue;
            }
            entries.push((entry.name, entry.scope));
        }
        let mut budget = MAX_INJECTED_BYTES;
        for (name, scope) in entries.into_iter().take(MAX_INJECTED_ENTRIES) {
            let Ok(content) = self.read(scope, &name) else {
                continue;
            };
            let mut body = content;
            if body.len() > MAX_INJECTED_ENTRY_BYTES {
                let mut end = MAX_INJECTED_ENTRY_BYTES;
                while !body.is_char_boundary(end) {
                    end -= 1;
                }
                body.truncate(end);
                body.push_str("\n[truncated]");
                injection.truncated = true;
            }
            let header = format!("### {name} ({})\n", scope.as_str());
            let separator = "\n\n";
            let allowance = budget
                .saturating_sub(header.len())
                .saturating_sub(separator.len());
            if allowance == 0 {
                break;
            }
            if body.len() > allowance {
                const MARKER: &str = "\n[truncated]";
                if allowance <= MARKER.len() {
                    let mut end = allowance;
                    while !body.is_char_boundary(end) {
                        end -= 1;
                    }
                    body.truncate(end);
                } else {
                    let mut end = allowance - MARKER.len();
                    while !body.is_char_boundary(end) {
                        end -= 1;
                    }
                    body.truncate(end);
                    body.push_str(MARKER);
                }
                injection.truncated = true;
            }
            budget = budget.saturating_sub(header.len() + body.len() + separator.len());
            injection.entries.push(name);
            injection.text.push_str(&header);
            injection.text.push_str(&body);
            injection.text.push_str(separator);
        }
        injection
    }

    fn entry_path(&self, scope: MemoryScope, name: &str) -> Result<PathBuf, ToolError> {
        validate_name(name)?;
        let root = self.root(scope)?;
        let path = root.join(name);
        if fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.file_type().is_symlink()) {
            return Err(ToolError::Policy(format!(
                "refusing to use symlinked memory entry {name}"
            )));
        }
        Ok(path)
    }

    /// The path an entry would occupy, without touching the filesystem.
    pub fn entry_path_for(&self, scope: MemoryScope, name: &str) -> Result<PathBuf, ToolError> {
        self.entry_path(scope, name)
    }
}

/// Resolves `relative` under `base` and refuses to leave `base`. The deepest
/// existing ancestor is canonicalized first, so a symlinked directory cannot
/// redirect the result.
fn resolve_under(base: &Path, relative: &str) -> Result<PathBuf, ToolError> {
    let base = base
        .canonicalize()
        .map_err(|error| ToolError::Io(format!("cannot resolve {}: {error}", base.display())))?;
    let candidate = base.join(relative);
    let mut existing = candidate.clone();
    let mut tail: Vec<OsString> = Vec::new();
    while !existing.exists() {
        let Some(name) = existing.file_name().map(|name| name.to_os_string()) else {
            break;
        };
        tail.push(name);
        if !existing.pop() {
            break;
        }
    }
    let mut resolved = existing.canonicalize().map_err(|error| {
        ToolError::Io(format!("cannot resolve {}: {error}", existing.display()))
    })?;
    for component in tail.iter().rev() {
        resolved.push(component);
    }
    if !resolved.starts_with(&base) {
        return Err(ToolError::Policy(format!(
            "memory directory escapes {}: {}",
            base.display(),
            resolved.display()
        )));
    }
    Ok(resolved)
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
        let store = MemoryStore::new(root, None);

        match action {
            MemoryAction::List => {
                let names: Vec<String> = store
                    .list(Some(MemoryScope::Workspace))
                    .into_iter()
                    .map(|entry| entry.name)
                    .collect();
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
            MemoryAction::Read => {
                let name = required_name(args)?;
                let content = store.read(MemoryScope::Workspace, name)?;
                Ok(ToolOutcome::ok(
                    format!("read {name}"),
                    truncate_text(&content, context.max_output_bytes),
                ))
            }
            MemoryAction::Write => {
                let name = required_name(args)?;
                let content = string_arg(args, "content")?;
                let (old, _) = store.write(MemoryScope::Workspace, name, content)?;
                let summary = format!("wrote {name} ({} bytes)", content.len());
                Ok(
                    ToolOutcome::ok(summary.clone(), summary).with_diff(Some(diff::unified_diff(
                        &format!("{MEMORY_DIR}/{name}"),
                        &old,
                        content,
                    ))),
                )
            }
            MemoryAction::Delete => {
                let name = required_name(args)?;
                store.delete(MemoryScope::Workspace, name)?;
                Ok(ToolOutcome::ok(
                    format!("deleted {name}"),
                    format!("deleted {name}"),
                ))
            }
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

    fn store_for(workspace: &Path, home: Option<&Path>) -> MemoryStore {
        MemoryStore::resolve(workspace, home).unwrap()
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
        assert!(deleted.success);
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

    #[test]
    fn store_lists_both_scopes_with_workspace_first() {
        let temp = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(workspace.join(".kl/memory")).unwrap();
        std::fs::create_dir_all(home.path().join("memory")).unwrap();
        std::fs::write(workspace.join(".kl/memory/a.md"), "workspace").unwrap();
        std::fs::write(home.path().join("memory/b.md"), "user").unwrap();

        let store = store_for(&workspace, Some(home.path()));
        let entries = store.list(None);
        assert_eq!(entries.len(), 2);
        assert_eq!(entries[0].name, "a.md");
        assert_eq!(entries[0].scope, MemoryScope::Workspace);
        assert_eq!(entries[1].name, "b.md");
        assert_eq!(entries[1].scope, MemoryScope::User);
    }

    #[test]
    fn empty_store_lists_nothing() {
        let temp = tempfile::tempdir().unwrap();
        let store = store_for(temp.path(), None);
        assert!(store.list(None).is_empty());
        assert!(store.injection().text.is_empty());
    }

    #[test]
    fn workspace_entry_shadows_user_entry() {
        let temp = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(workspace.join(".kl/memory")).unwrap();
        std::fs::create_dir_all(home.path().join("memory")).unwrap();
        std::fs::write(workspace.join(".kl/memory/dup.md"), "from workspace").unwrap();
        std::fs::write(home.path().join("memory/dup.md"), "from user").unwrap();

        let store = store_for(&workspace, Some(home.path()));
        let injection = store.injection();
        assert!(injection.text.contains("from workspace"));
        assert!(!injection.text.contains("from user"));
        assert_eq!(injection.entries, vec!["dup.md".to_owned()]);
    }

    #[test]
    fn injection_respects_the_size_budget() {
        let temp = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(workspace.join(".kl/memory")).unwrap();
        std::fs::create_dir_all(home.path().join("memory")).unwrap();
        for index in 0..40 {
            std::fs::write(
                workspace.join(format!(".kl/memory/note{index}.md")),
                "x".repeat(MAX_INJECTED_ENTRY_BYTES),
            )
            .unwrap();
        }

        let store = store_for(&workspace, Some(home.path()));
        let injection = store.injection();
        assert!(injection.text.len() <= MAX_INJECTED_BYTES + 2);
        assert!(injection.entries.len() <= MAX_INJECTED_ENTRIES);
        assert!(injection.truncated);
    }

    #[test]
    fn store_write_and_delete_are_scope_aware() {
        let temp = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let workspace = temp.path().join("workspace");
        std::fs::create_dir_all(&workspace).unwrap();
        std::fs::create_dir_all(home.path()).unwrap();

        let store = store_for(&workspace, Some(home.path()));
        store
            .write(MemoryScope::User, "user.md", "user content")
            .unwrap();
        assert!(home.path().join("memory/user.md").is_file());
        assert!(!workspace.join(".kl/memory/user.md").exists());

        let error = store
            .write(MemoryScope::Workspace, "../escape.md", "x")
            .unwrap_err();
        assert!(matches!(error, ToolError::InvalidArgs(_)), "{error}");

        store.delete(MemoryScope::User, "user.md").unwrap();
        assert!(!home.path().join("memory/user.md").exists());
    }
}
