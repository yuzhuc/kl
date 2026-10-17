use crate::{
    PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, optional_bool, optional_string,
    optional_usize, string_arg, truncate_text,
};
use globset::{Glob, GlobMatcher};
use kl_policy::{Action, Effect};
use regex::{Regex, RegexBuilder};
use serde_json::{Value, json};
use std::path::Path;
use walkdir::WalkDir;

pub struct GrepTool;

impl Tool for GrepTool {
    fn name(&self) -> &'static str {
        "grep"
    }

    fn description(&self) -> &'static str {
        "Search workspace text files with a regular expression."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "pattern": {"type": "string", "description": "Rust regex pattern"},
                "path": {"type": "string", "default": "."},
                "glob": {"type": "string", "description": "Optional file glob such as *.rs"},
                "ignore_case": {"type": "boolean", "default": false},
                "max_results": {"type": "integer", "default": 100, "maximum": 1000}
            },
            "required": ["pattern"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Read,
            resource: optional_string(args, "path").unwrap_or(".").to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let pattern = string_arg(args, "pattern")?;
        let raw_path = optional_string(args, "path").unwrap_or(".");
        let evaluation = context.policy.evaluate(Action::Read, raw_path);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let root = evaluation
            .resolved
            .ok_or_else(|| ToolError::Io("path was not resolved".to_owned()))?;
        let regex = compile_regex(pattern, optional_bool(args, "ignore_case"))?;
        let matcher = optional_string(args, "glob")
            .map(build_matcher)
            .transpose()?;
        let max_results = optional_usize(args, "max_results")
            .unwrap_or(100)
            .clamp(1, 1000);

        let mut results = Vec::new();
        let mut limit_reached = false;
        'walk: for entry in WalkDir::new(&root)
            .follow_links(false)
            .into_iter()
            .filter_entry(should_descend)
            .filter_map(Result::ok)
        {
            if !entry.file_type().is_file() {
                continue;
            }
            let relative = relative_path(&root, entry.path());
            if let Some(matcher) = &matcher {
                if !matcher.is_match(&relative) {
                    continue;
                }
            }
            let Some(text) = read_text(entry.path()) else {
                continue;
            };
            for (index, line) in text.lines().enumerate() {
                if regex.is_match(line) {
                    results.push(format!(
                        "{}:{}: {}",
                        relative,
                        index + 1,
                        truncate_line(line.trim_end_matches('\r'), 400)
                    ));
                    if results.len() >= max_results {
                        limit_reached = true;
                        break 'walk;
                    }
                }
            }
        }

        let summary = if results.is_empty() {
            format!("no matches for `{pattern}`")
        } else {
            format!(
                "{} match{} for `{pattern}`{}",
                results.len(),
                if results.len() == 1 { "" } else { "es" },
                if limit_reached {
                    " (limit reached)"
                } else {
                    ""
                }
            )
        };
        Ok(ToolOutcome::ok(
            summary,
            truncate_text(&results.join("\n"), context.max_output_bytes),
        ))
    }
}

pub struct GlobTool;

impl Tool for GlobTool {
    fn name(&self) -> &'static str {
        "glob"
    }

    fn description(&self) -> &'static str {
        "List workspace files matching a glob pattern."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "pattern": {"type": "string"},
                "path": {"type": "string", "default": "."},
                "max_results": {"type": "integer", "default": 500, "maximum": 5000}
            },
            "required": ["pattern"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Read,
            resource: optional_string(args, "path").unwrap_or(".").to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let pattern = string_arg(args, "pattern")?;
        let raw_path = optional_string(args, "path").unwrap_or(".");
        let evaluation = context.policy.evaluate(Action::Read, raw_path);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let root = evaluation
            .resolved
            .ok_or_else(|| ToolError::Io("path was not resolved".to_owned()))?;
        let matcher = build_matcher(pattern)?;
        let max_results = optional_usize(args, "max_results")
            .unwrap_or(500)
            .clamp(1, 5000);

        let mut files = Vec::new();
        for entry in WalkDir::new(&root)
            .follow_links(false)
            .into_iter()
            .filter_entry(should_descend)
            .filter_map(Result::ok)
        {
            if !entry.file_type().is_file() {
                continue;
            }
            let relative = relative_path(&root, entry.path());
            if matcher.is_match(&relative) {
                files.push(relative);
                if files.len() >= max_results {
                    break;
                }
            }
        }
        files.sort();

        let summary = format!(
            "{} file{} matched `{pattern}`",
            files.len(),
            plural(files.len())
        );
        Ok(ToolOutcome::ok(
            summary,
            truncate_text(&files.join("\n"), context.max_output_bytes),
        ))
    }
}

fn compile_regex(pattern: &str, ignore_case: bool) -> Result<Regex, ToolError> {
    RegexBuilder::new(pattern)
        .case_insensitive(ignore_case)
        .build()
        .map_err(|error| ToolError::InvalidArgs(format!("invalid regex: {error}")))
}

fn build_matcher(pattern: &str) -> Result<GlobMatcher, ToolError> {
    let normalized = if pattern.contains('/') {
        pattern.to_owned()
    } else {
        format!("**/{pattern}")
    };
    Glob::new(&normalized)
        .map(|glob| glob.compile_matcher())
        .map_err(|error| ToolError::InvalidArgs(format!("invalid glob: {error}")))
}

fn should_descend(entry: &walkdir::DirEntry) -> bool {
    if !entry.file_type().is_dir() {
        return true;
    }
    let name = entry.file_name().to_string_lossy().to_lowercase();
    !matches!(
        name.as_str(),
        ".git"
            | ".hg"
            | ".svn"
            | "target"
            | "node_modules"
            | ".venv"
            | "dist"
            | "build"
            | ".idea"
            | ".vs"
    )
}

fn relative_path(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or(path)
        .to_string_lossy()
        .replace('\\', "/")
}

fn read_text(path: &Path) -> Option<String> {
    let metadata = std::fs::metadata(path).ok()?;
    if metadata.len() > 2 * 1024 * 1024 {
        return None;
    }
    let bytes = std::fs::read(path).ok()?;
    if bytes.iter().take(8192).any(|byte| *byte == 0) {
        return None;
    }
    Some(String::from_utf8_lossy(&bytes).into_owned())
}

fn truncate_line(line: &str, limit: usize) -> String {
    if line.chars().count() <= limit {
        return line.to_owned();
    }
    let text: String = line.chars().take(limit).collect();
    format!("{text}...")
}

fn plural(count: usize) -> &'static str {
    if count == 1 { "" } else { "s" }
}
