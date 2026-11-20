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
    // Codex keeps built-in skills in `skills/.system/<name>`.
    collect_skills(&home.join("skills").join(".system"), &mut skills);
    collect_skills(&workspace.join(".kl").join("skills"), &mut skills);
    skills.into_values().collect()
}

const SKILL_CREATOR: &str = "---
name: skill-creator
description: Create or revise a KL skill as a reusable instruction file.
---

# Skill Creator

A skill is a directory with a `SKILL.md` file. KL discovers skills in
`~/.kl/skills/<name>/` and `<workspace>/.kl/skills/<name>/` and lists each
one's name and description; the body is only read when the skill is used.

## Steps

1. Choose a short lowercase name made of ASCII letters, digits and hyphens.
2. Create `~/.kl/skills/<name>/SKILL.md` for a personal skill, or
   `<workspace>/.kl/skills/<name>/SKILL.md` to share it with the repository.
3. Start the file with frontmatter holding `name` and `description` (one
   line, no quotes needed), then explain when to use the skill and the exact
   steps to follow.
4. Keep the body short and concrete. Put long references in separate files
   next to `SKILL.md`.
5. List skills and read the new one back to verify it.
";

/// Installs the built-in skills once; user edits are never overwritten.
pub fn install_system_skills(home: &Path) {
    let path = home
        .join("skills")
        .join(".system")
        .join("skill-creator")
        .join(SKILL_FILE);
    if path.exists() {
        return;
    }
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let _ = fs::write(&path, SKILL_CREATOR);
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
mod tests;
