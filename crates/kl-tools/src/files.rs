use crate::{
    PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, diff, optional_bool, optional_usize,
    string_arg, truncate_text,
};
use kl_policy::{Action, Effect};
use serde_json::{Value, json};
use std::fs;

pub struct ReadFileTool;

impl Tool for ReadFileTool {
    fn name(&self) -> &'static str {
        "read_file"
    }

    fn description(&self) -> &'static str {
        "Read a text file inside the workspace. Returns numbered lines."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path relative to the workspace"},
                "offset": {"type": "integer", "minimum": 1, "description": "First line (1-based)"},
                "limit": {"type": "integer", "minimum": 1, "description": "Maximum lines (default 2000)"}
            },
            "required": ["path"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Read,
            resource: string_arg(args, "path")?.to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let path = string_arg(args, "path")?;
        let evaluation = context.policy.evaluate(Action::Read, path);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let resolved = evaluation
            .resolved
            .ok_or_else(|| ToolError::Io("path was not resolved".to_owned()))?;
        let bytes = fs::read(&resolved).map_err(|error| {
            ToolError::Io(format!("cannot read {}: {error}", resolved.display()))
        })?;
        if bytes.contains(&0) {
            return Err(ToolError::Io(format!("{path} appears to be binary")));
        }

        let text = String::from_utf8_lossy(&bytes);
        let offset = optional_usize(args, "offset").unwrap_or(1).max(1);
        let limit = optional_usize(args, "limit").unwrap_or(2000).min(5000);
        let lines: Vec<&str> = text.lines().collect();
        if offset > lines.len() {
            return Ok(ToolOutcome::ok(
                format!("0 lines from {path} (offset {offset} past end)"),
                "",
            ));
        }
        let end = (offset - 1 + limit).min(lines.len());
        let body = lines[offset - 1..end]
            .iter()
            .enumerate()
            .map(|(index, line)| format!("{:>6}: {}", offset + index, line.trim_end_matches('\r')))
            .collect::<Vec<_>>()
            .join("\n");
        let output = truncate_text(&body, context.max_output_bytes);
        Ok(ToolOutcome::ok(
            format!("read {} lines from {path}", end - offset + 1),
            output,
        ))
    }
}

pub struct WriteFileTool;

impl Tool for WriteFileTool {
    fn name(&self) -> &'static str {
        "write_file"
    }

    fn description(&self) -> &'static str {
        "Create or replace a text file inside the workspace."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "content": {"type": "string"}
            },
            "required": ["path", "content"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Edit,
            resource: string_arg(args, "path")?.to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let path = string_arg(args, "path")?;
        let content = string_arg(args, "content")?;
        let evaluation = context.policy.evaluate(Action::Edit, path);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let resolved = evaluation
            .resolved
            .ok_or_else(|| ToolError::Io("path was not resolved".to_owned()))?;

        let old = fs::read(&resolved)
            .map(|bytes| String::from_utf8_lossy(&bytes).into_owned())
            .unwrap_or_default();
        if old == content {
            return Ok(ToolOutcome::ok(format!("{path} unchanged"), ""));
        }

        if let Some(parent) = resolved.parent() {
            fs::create_dir_all(parent).map_err(|error| {
                ToolError::Io(format!("cannot create {}: {error}", parent.display()))
            })?;
        }
        fs::write(&resolved, content).map_err(|error| {
            ToolError::Io(format!("cannot write {}: {error}", resolved.display()))
        })?;
        let summary = format!("wrote {} bytes to {path}", content.len());
        Ok(ToolOutcome::ok(summary.clone(), summary)
            .with_diff(Some(diff::unified_diff(path, &old, content))))
    }
}

pub struct EditFileTool;

impl Tool for EditFileTool {
    fn name(&self) -> &'static str {
        "edit_file"
    }

    fn description(&self) -> &'static str {
        "Replace an exact string in a workspace file. Fails if the match is ambiguous."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "path": {"type": "string"},
                "old_string": {"type": "string"},
                "new_string": {"type": "string"},
                "replace_all": {"type": "boolean", "default": false}
            },
            "required": ["path", "old_string", "new_string"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Edit,
            resource: string_arg(args, "path")?.to_owned(),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let path = string_arg(args, "path")?;
        let old_string = string_arg(args, "old_string")?;
        let new_string = string_arg(args, "new_string")?;
        if old_string.is_empty() {
            return Err(ToolError::InvalidArgs(
                "`old_string` must not be empty".to_owned(),
            ));
        }
        let replace_all = optional_bool(args, "replace_all");
        let evaluation = context.policy.evaluate(Action::Edit, path);
        if evaluation.effect == Effect::Deny {
            return Err(ToolError::Policy(evaluation.reason));
        }
        let resolved = evaluation
            .resolved
            .ok_or_else(|| ToolError::Io("path was not resolved".to_owned()))?;
        let bytes = fs::read(&resolved).map_err(|error| {
            ToolError::Io(format!("cannot read {}: {error}", resolved.display()))
        })?;
        if bytes.contains(&0) {
            return Err(ToolError::Io(format!("{path} appears to be binary")));
        }
        let old_text = String::from_utf8_lossy(&bytes).into_owned();
        let matches = old_text.matches(old_string).count();
        if matches == 0 {
            return Err(ToolError::NotFound(format!(
                "`old_string` not found in {path}"
            )));
        }
        if matches > 1 && !replace_all {
            return Err(ToolError::InvalidArgs(format!(
                "`old_string` matches {matches} times in {path}; pass replace_all or a longer anchor"
            )));
        }
        let new_text = if replace_all {
            old_text.replace(old_string, new_string)
        } else {
            old_text.replacen(old_string, new_string, 1)
        };
        fs::write(&resolved, &new_text).map_err(|error| {
            ToolError::Io(format!("cannot write {}: {error}", resolved.display()))
        })?;
        let summary = format!(
            "edited {path} ({} replacement{})",
            matches,
            if matches == 1 { "" } else { "s" }
        );
        Ok(ToolOutcome::ok(summary.clone(), summary)
            .with_diff(Some(diff::unified_diff(path, &old_text, &new_text))))
    }
}
