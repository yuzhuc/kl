use kl_policy::Action;
use kl_tools::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome};
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

const SUMMARY_MAX_BYTES: usize = 4 * 1024;
const SKILL_FILE: &str = "SKILL.md";
const BODY_MAX_BYTES: usize = 64 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SkillSummary {
    pub name: String,
    pub description: String,
    pub path: PathBuf,
}

pub fn discover(home: &Path, workspace: &Path) -> Vec<SkillSummary> {
    let mut skills = BTreeMap::new();
    collect_skills(&home.join("skills"), &mut skills);
    collect_skills(&workspace.join(".kl").join("skills"), &mut skills);
    skills.into_values().collect()
}

pub fn summary_prompt(skills: &[SkillSummary]) -> String {
    let mut prompt = String::new();
    for skill in skills {
        let line = format!("{}: {}\n", skill.name, skill.description);
        let remaining = SUMMARY_MAX_BYTES.saturating_sub(prompt.len());
        if remaining == 0 {
            break;
        }
        if line.len() <= remaining {
            prompt.push_str(&line);
            continue;
        }
        let mut end = remaining;
        while !line.is_char_boundary(end) {
            end -= 1;
        }
        prompt.push_str(&line[..end]);
        break;
    }
    prompt
}

pub(crate) fn load_body(home: &Path, workspace: &Path, name: &str) -> Result<String, ToolError> {
    validate_skill_name(name)?;
    let skill = discover(home, workspace)
        .into_iter()
        .find(|skill| skill.name == name)
        .ok_or_else(|| ToolError::NotFound(format!("unknown skill: {name}")))?;
    let bytes = fs::read(&skill.path)
        .map_err(|error| ToolError::Io(format!("cannot read {}: {error}", skill.path.display())))?;
    let text = String::from_utf8_lossy(&bytes);
    let (_, body) = split_frontmatter(&text)
        .ok_or_else(|| ToolError::Io(format!("{name} has no frontmatter")))?;
    Ok(truncate_body(body))
}

fn truncate_body(body: &str) -> String {
    if body.len() <= BODY_MAX_BYTES {
        return body.to_owned();
    }
    let mut end = BODY_MAX_BYTES;
    while !body.is_char_boundary(end) {
        end -= 1;
    }
    let mut truncated = body[..end].to_owned();
    truncated.push_str("\n[skill truncated]");
    truncated
}

pub(crate) struct SkillTool {
    pub(crate) home: PathBuf,
}

impl SkillTool {
    fn find(&self, workspace: &Path, name: &str) -> Result<SkillSummary, ToolError> {
        discover(&self.home, workspace)
            .into_iter()
            .find(|skill| skill.name == name)
            .ok_or_else(|| ToolError::NotFound(format!("unknown skill: {name}")))
    }
}

impl Tool for SkillTool {
    fn name(&self) -> &'static str {
        "skill"
    }

    fn description(&self) -> &'static str {
        "Load the full instructions for a discovered skill by name."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "name": {"type": "string", "description": "Skill name from the available skills list"}
            },
            "required": ["name"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let workspace = std::env::current_dir()
            .map_err(|error| ToolError::Io(format!("cannot resolve workspace: {error}")))?;
        self.policy_request_for(&workspace, args)
    }

    fn policy_request_for(
        &self,
        workspace: &Path,
        args: &Value,
    ) -> Result<PolicyRequest, ToolError> {
        let name = skill_name(args)?;
        self.find(workspace, name)?;
        // Discovered skills are trusted roots; use the in-workspace root so home skills are not rejected as outside the workspace.
        Ok(PolicyRequest {
            action: Action::Read,
            resource: format!(".kl/skills/{name}"),
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        skill_name(args).map(|_| ())
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let name = skill_name(args)?;
        let body = load_body(&self.home, context.policy.workspace(), name)?;
        Ok(ToolOutcome::ok(
            format!("loaded skill {name}"),
            format!("<skill_content name=\"{name}\">\n{body}\n</skill_content>"),
        ))
    }

    fn read_only(&self, _args: &Value) -> bool {
        true
    }

    fn max_result_chars(&self) -> usize {
        20_000
    }
}

fn collect_skills(directory: &Path, skills: &mut BTreeMap<String, SkillSummary>) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let mut directories: Vec<PathBuf> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .collect();
    directories.sort();

    for directory in directories {
        let Ok(metadata) = fs::symlink_metadata(&directory) else {
            continue;
        };
        if !metadata.file_type().is_dir() || metadata.file_type().is_symlink() {
            continue;
        }
        let Some(directory_name) = directory.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        let path = directory.join(SKILL_FILE);
        let Ok(metadata) = fs::symlink_metadata(&path) else {
            continue;
        };
        if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
            continue;
        }
        let Ok(content) = fs::read_to_string(&path) else {
            continue;
        };
        let Some((name, description)) = parse_summary(&content, directory_name) else {
            continue;
        };
        skills.insert(
            name.clone(),
            SkillSummary {
                name,
                description,
                path,
            },
        );
    }
}

fn parse_summary(content: &str, directory_name: &str) -> Option<(String, String)> {
    let (frontmatter, _) = split_frontmatter(content)?;
    let mut name = None;
    let mut description = None;
    for line in frontmatter.lines() {
        let Some((key, value)) = line.split_once(':') else {
            continue;
        };
        match key.trim() {
            "name" => name = Some(unquote(value.trim())),
            "description" => description = Some(unquote(value.trim())),
            _ => {}
        }
    }
    let name = name
        .filter(|name| !name.is_empty())
        .unwrap_or(directory_name);
    let description = description.filter(|description| !description.is_empty())?;
    if !valid_name(name) {
        return None;
    }
    Some((name.to_owned(), description.to_owned()))
}

fn split_frontmatter(content: &str) -> Option<(&str, &str)> {
    let content = content.strip_prefix('\u{feff}').unwrap_or(content);
    let (first, rest) = content.split_once('\n')?;
    if first.trim_end_matches('\r').trim() != "---" {
        return None;
    }
    let mut offset = 0;
    for line in rest.split_inclusive('\n') {
        if line.trim_end_matches(['\r', '\n']).trim() == "---" {
            return Some((&rest[..offset], &rest[offset + line.len()..]));
        }
        offset += line.len();
    }
    None
}

fn skill_name(args: &Value) -> Result<&str, ToolError> {
    let name = args
        .get("name")
        .and_then(Value::as_str)
        .ok_or_else(|| ToolError::InvalidArgs("`name` must be a string".to_owned()))?;
    validate_skill_name(name)?;
    Ok(name)
}

fn validate_skill_name(name: &str) -> Result<(), ToolError> {
    if !valid_name(name) {
        return Err(ToolError::InvalidArgs(format!(
            "invalid skill name: {name}"
        )));
    }
    Ok(())
}

fn valid_name(name: &str) -> bool {
    !name.is_empty()
        && name
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

fn unquote(value: &str) -> &str {
    if value.len() >= 2
        && ((value.starts_with('"') && value.ends_with('"'))
            || (value.starts_with('\'') && value.ends_with('\'')))
    {
        &value[1..value.len() - 1]
    } else {
        value
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::{PolicyConfig, PolicyEngine};
    use kl_sandbox::SandboxManager;
    use std::sync::Arc;
    use std::sync::atomic::AtomicBool;
    use std::time::Duration;

    fn write_skill(root: &Path, directory: &str, content: &str) -> PathBuf {
        let path = root.join(directory).join(SKILL_FILE);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, content).unwrap();
        path
    }

    fn skill_content(name: &str, description: &str, body: &str) -> String {
        format!("---\nname: {name}\ndescription: {description}\n---\n{body}")
    }

    #[test]
    fn discovers_home_and_workspace_with_workspace_override() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        write_skill(
            &home.path().join("skills"),
            "alpha",
            &skill_content("alpha", "home alpha", "HOME BODY"),
        );
        write_skill(
            &workspace.path().join(".kl/skills"),
            "alpha",
            &skill_content("alpha", "workspace alpha", "WORKSPACE BODY"),
        );

        let skills = discover(home.path(), workspace.path());
        let alpha = skills.iter().find(|skill| skill.name == "alpha").unwrap();
        assert_eq!(alpha.description, "workspace alpha");
        assert_eq!(skills.len(), 1);
    }

    #[test]
    fn uses_directory_name_and_skips_missing_frontmatter() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        write_skill(
            &home.path().join("skills"),
            "from-directory",
            "---\ndescription: fallback name\n---\nBODY",
        );
        write_skill(
            &home.path().join("skills"),
            "missing",
            "description: no frontmatter\nBODY",
        );

        let skills = discover(home.path(), workspace.path());
        assert_eq!(skills.len(), 1);
        assert_eq!(skills[0].name, "from-directory");
    }

    #[test]
    fn summary_excludes_body_and_path_and_is_bounded() {
        let skill = SkillSummary {
            name: "alpha".to_owned(),
            description: "x".repeat(SUMMARY_MAX_BYTES + 100),
            path: PathBuf::from("C:/secret/path/SKILL.md"),
        };
        let summary = summary_prompt(&[skill]);
        assert!(summary.len() <= SUMMARY_MAX_BYTES);
        assert!(summary.starts_with("alpha: "));
        assert!(!summary.contains("secret/path"));
        assert!(!summary.contains("BODY"));
    }

    fn context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
        ToolContext {
            policy,
            cancel,
            sandbox: Arc::new(SandboxManager::policy_only()),
            timeout: Duration::from_secs(5),
            max_output_bytes: 64 * 1024,
        }
    }

    #[test]
    fn skill_tool_loads_body_by_name() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        write_skill(
            &workspace.path().join(".kl/skills"),
            "alpha",
            &skill_content("alpha", "Alpha description", "Run the checks.\n"),
        );
        let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let tool = SkillTool {
            home: home.path().to_path_buf(),
        };

        let request = tool
            .policy_request_for(workspace.path(), &json!({"name": "alpha"}))
            .unwrap();
        assert_eq!(request.action, Action::Read);
        assert_eq!(request.resource, ".kl/skills/alpha");
        assert_eq!(
            policy.evaluate(request.action, &request.resource).effect,
            kl_policy::Effect::Allow
        );

        let outcome = tool
            .execute(&context(&policy, &cancel), &json!({"name": "alpha"}))
            .unwrap();
        assert!(outcome.output.starts_with("<skill_content name=\"alpha\">"));
        assert!(outcome.output.contains("Run the checks."));
        assert!(!outcome.output.contains("description: Alpha description"));
    }

    #[test]
    fn load_body_loads_trusted_home_skill() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        write_skill(
            &home.path().join("skills"),
            "alpha",
            &skill_content("alpha", "Alpha description", "HOME BODY"),
        );

        assert_eq!(
            load_body(home.path(), workspace.path(), "alpha").unwrap(),
            "HOME BODY"
        );
    }

    #[test]
    fn load_body_truncates_long_body_on_char_boundary() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        let body = format!("{}TAIL", "界".repeat(BODY_MAX_BYTES));
        write_skill(
            &home.path().join("skills"),
            "big",
            &skill_content("big", "Big description", &body),
        );

        let loaded = load_body(home.path(), workspace.path(), "big").unwrap();
        assert!(loaded.ends_with("[skill truncated]"));
        assert!(!loaded.contains("TAIL"));
        assert!(loaded.len() <= BODY_MAX_BYTES + "\n[skill truncated]".len());
    }

    #[test]
    fn skill_tool_loads_home_skill_body() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        write_skill(
            &home.path().join("skills"),
            "alpha",
            &skill_content("alpha", "Alpha description", "HOME BODY"),
        );
        let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let tool = SkillTool {
            home: home.path().to_path_buf(),
        };

        let request = tool
            .policy_request_for(workspace.path(), &json!({"name": "alpha"}))
            .unwrap();
        assert_eq!(
            policy.evaluate(request.action, &request.resource).effect,
            kl_policy::Effect::Allow
        );
        let outcome = tool
            .execute(&context(&policy, &cancel), &json!({"name": "alpha"}))
            .unwrap();
        assert!(outcome.output.contains("HOME BODY"));
    }

    #[test]
    fn skill_tool_rejects_unknown_and_invalid_names() {
        let home = tempfile::tempdir().unwrap();
        let workspace = tempfile::tempdir().unwrap();
        let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let tool = SkillTool {
            home: home.path().to_path_buf(),
        };

        let unknown = tool
            .execute(&context(&policy, &cancel), &json!({"name": "missing"}))
            .unwrap_err();
        assert!(unknown.to_string().contains("unknown skill: missing"));

        let invalid = tool
            .execute(&context(&policy, &cancel), &json!({"name": "../missing"}))
            .unwrap_err();
        assert!(invalid.to_string().contains("invalid skill name"));
    }
}
