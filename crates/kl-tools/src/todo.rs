//! Workspace todo list stored in `.kl/todos.json`.

use crate::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, string_arg, truncate_text};
use kl_policy::{Action, Effect};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

const TODO_PATH: &str = ".kl/todos.json";
const TODO_DIR: &str = ".kl";
const MAX_TODO_FILE_BYTES: usize = 256 * 1024;
const STATUS_PENDING: &str = "pending";
const STATUS_COMPLETED: &str = "completed";

pub struct TodoTool;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TodoAction {
    List,
    Add,
    Update,
    Complete,
}

impl TodoAction {
    fn parse(value: &str) -> Result<Self, ToolError> {
        match value {
            "list" => Ok(Self::List),
            "add" => Ok(Self::Add),
            "update" => Ok(Self::Update),
            "complete" => Ok(Self::Complete),
            other => Err(ToolError::InvalidArgs(format!(
                "`action` must be list, add, update or complete (got {other})"
            ))),
        }
    }
}

impl Tool for TodoTool {
    fn name(&self) -> &str {
        "todo"
    }

    fn description(&self) -> &str {
        "Track a workspace todo list stored in .kl/todos.json."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "action": {
                    "type": "string",
                    "enum": ["list", "add", "update", "complete"]
                },
                "id": {
                    "type": "string",
                    "description": "Todo id; required for update and complete"
                },
                "title": {
                    "type": "string",
                    "description": "Todo title; required for add, optional for update"
                },
                "status": {
                    "type": "string",
                    "enum": ["pending", "completed"],
                    "description": "New status; only used by update"
                }
            },
            "required": ["action"],
            "additionalProperties": false
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        match parse_action(args)? {
            TodoAction::List => {}
            TodoAction::Add => {
                title_arg(args)?;
            }
            TodoAction::Update => {
                update_args(args)?;
            }
            TodoAction::Complete => {
                id_arg(args)?;
            }
        }
        Ok(())
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        let action = parse_action(args)?;
        Ok(PolicyRequest {
            action: match action {
                TodoAction::List => Action::Read,
                TodoAction::Add | TodoAction::Update | TodoAction::Complete => Action::Edit,
            },
            resource: TODO_PATH.to_owned(),
        })
    }

    fn read_only(&self, args: &Value) -> bool {
        parse_action(args).is_ok_and(|action| action == TodoAction::List)
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

        let path = todo_path(context)?;
        let mut todos = load(&path)?;
        match action {
            TodoAction::List => {
                let output = render(&todos, context)?;
                let summary = format!(
                    "{} todo{}",
                    todos.len(),
                    if todos.len() == 1 { "" } else { "s" }
                );
                Ok(ToolOutcome::ok(summary, output))
            }
            TodoAction::Add => {
                let title = title_arg(args)?;
                let id = next_id(&todos);
                let todo = json!({
                    "id": id,
                    "title": title,
                    "status": STATUS_PENDING
                });
                todos.push(todo.clone());
                save(&path, &todos)?;
                Ok(ToolOutcome::ok(
                    format!("added todo {id}"),
                    render(&[todo], context)?,
                ))
            }
            TodoAction::Update => {
                let (id, title, status) = update_args(args)?;
                let index = find(&todos, id)?;
                let mut todo =
                    todos[index].clone().as_object().cloned().ok_or_else(|| {
                        ToolError::InvalidArgs("todo store is malformed".to_owned())
                    })?;
                if let Some(title) = title {
                    todo.insert("title".to_owned(), Value::String(title.to_owned()));
                }
                if let Some(status) = status {
                    todo.insert("status".to_owned(), Value::String(status.to_owned()));
                }
                todos[index] = Value::Object(todo);
                save(&path, &todos)?;
                Ok(ToolOutcome::ok(
                    format!("updated todo {id}"),
                    render(&[todos[index].clone()], context)?,
                ))
            }
            TodoAction::Complete => {
                let id = id_arg(args)?;
                let index = find(&todos, id)?;
                let mut todo =
                    todos[index].clone().as_object().cloned().ok_or_else(|| {
                        ToolError::InvalidArgs("todo store is malformed".to_owned())
                    })?;
                todo.insert(
                    "status".to_owned(),
                    Value::String(STATUS_COMPLETED.to_owned()),
                );
                todos[index] = Value::Object(todo);
                save(&path, &todos)?;
                Ok(ToolOutcome::ok(
                    format!("completed todo {id}"),
                    render(&[todos[index].clone()], context)?,
                ))
            }
        }
    }
}

fn parse_action(args: &Value) -> Result<TodoAction, ToolError> {
    TodoAction::parse(string_arg(args, "action")?)
}

fn id_arg(args: &Value) -> Result<&str, ToolError> {
    string_arg(args, "id")
}

fn title_arg(args: &Value) -> Result<&str, ToolError> {
    let title = string_arg(args, "title")?.trim();
    if title.is_empty() {
        return Err(ToolError::InvalidArgs(
            "`title` must not be empty".to_owned(),
        ));
    }
    Ok(title)
}

fn status_value(value: &Value) -> Result<&str, ToolError> {
    let status = value
        .as_str()
        .ok_or_else(|| ToolError::InvalidArgs("`status` must be a string".to_owned()))?;
    if status != STATUS_PENDING && status != STATUS_COMPLETED {
        return Err(ToolError::InvalidArgs(format!(
            "`status` must be {STATUS_PENDING} or {STATUS_COMPLETED} (got {status})"
        )));
    }
    Ok(status)
}

/// Validated arguments of an update: the todo id plus the fields to change.
fn update_args(args: &Value) -> Result<(&str, Option<&str>, Option<&str>), ToolError> {
    let id = id_arg(args)?;
    let title = match args.get("title") {
        Some(_) => Some(title_arg(args)?),
        None => None,
    };
    let status = match args.get("status") {
        Some(status) => Some(status_value(status)?),
        None => None,
    };
    if title.is_none() && status.is_none() {
        return Err(ToolError::InvalidArgs(
            "update needs `title` or `status`".to_owned(),
        ));
    }
    Ok((id, title, status))
}

fn find(todos: &[Value], id: &str) -> Result<usize, ToolError> {
    todos
        .iter()
        .position(|todo| todo.get("id").and_then(Value::as_str) == Some(id))
        .ok_or_else(|| ToolError::NotFound(format!("todo `{id}`")))
}

fn next_id(todos: &[Value]) -> String {
    let taken: Vec<String> = todos
        .iter()
        .filter_map(|todo| todo.get("id").and_then(Value::as_str))
        .map(str::to_owned)
        .collect();
    let mut next = taken
        .iter()
        .filter_map(|id| id.strip_prefix('t'))
        .filter_map(|number| number.parse::<u64>().ok())
        .max()
        .unwrap_or(0)
        + 1;
    while taken.contains(&format!("t{next}")) {
        next += 1;
    }
    format!("t{next}")
}

fn render(todos: &[Value], context: &ToolContext<'_>) -> Result<String, ToolError> {
    let text = serde_json::to_string_pretty(&Value::Array(todos.to_vec()))
        .map_err(|error| ToolError::Io(format!("cannot encode todos: {error}")))?;
    Ok(truncate_text(&text, context.max_output_bytes))
}

/// Path of the todo store. The `.kl` directory is resolved through the policy
/// engine so a symlinked `.kl` cannot redirect the store outside the
/// workspace, and a symlinked store file is refused outright.
fn todo_path(context: &ToolContext<'_>) -> Result<PathBuf, ToolError> {
    let dir = context
        .policy
        .resolve(TODO_DIR)
        .map_err(|error| ToolError::Policy(error.to_string()))?;
    if !dir.starts_with(context.policy.workspace()) {
        return Err(ToolError::Policy(format!(
            "todo directory escapes the workspace: {}",
            dir.display()
        )));
    }
    let path = dir.join("todos.json");
    if fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.file_type().is_symlink()) {
        return Err(ToolError::Policy(
            "refusing to use a symlinked todo store".to_owned(),
        ));
    }
    Ok(path)
}

fn load(path: &Path) -> Result<Vec<Value>, ToolError> {
    let metadata = match fs::metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(error) => {
            return Err(ToolError::Io(format!(
                "cannot read {}: {error}",
                path.display()
            )));
        }
    };
    if metadata.len() > MAX_TODO_FILE_BYTES as u64 {
        return Err(ToolError::InvalidArgs(format!(
            "todo store exceeds the {MAX_TODO_FILE_BYTES} byte limit"
        )));
    }
    let text = fs::read_to_string(path)
        .map_err(|error| ToolError::Io(format!("cannot read {}: {error}", path.display())))?;
    if text.trim().is_empty() {
        return Ok(Vec::new());
    }
    let value: Value = serde_json::from_str(&text).map_err(|error| {
        ToolError::InvalidArgs(format!("todo store is not valid JSON: {error}"))
    })?;
    match value.get("todos") {
        Some(Value::Array(todos)) => Ok(todos.clone()),
        None => Ok(Vec::new()),
        Some(_) => Err(ToolError::InvalidArgs(
            "todo store has an invalid `todos` field".to_owned(),
        )),
    }
}

fn save(path: &Path, todos: &[Value]) -> Result<(), ToolError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| {
            ToolError::Io(format!("cannot create {}: {error}", parent.display()))
        })?;
    }
    let text = serde_json::to_string_pretty(&json!({ "todos": todos }))
        .map_err(|error| ToolError::Io(format!("cannot encode todos: {error}")))?;
    if text.len() > MAX_TODO_FILE_BYTES {
        return Err(ToolError::InvalidArgs(format!(
            "todo store exceeds the {MAX_TODO_FILE_BYTES} byte limit"
        )));
    }
    fs::write(path, text)
        .map_err(|error| ToolError::Io(format!("cannot write {}: {error}", path.display())))
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
    fn add_list_update_complete_round_trip() {
        let (temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = TodoTool;

        tool.execute(
            &context,
            &json!({"action": "add", "title": "write the tests"}),
        )
        .unwrap();
        tool.execute(
            &context,
            &json!({"action": "add", "title": "ship the release"}),
        )
        .unwrap();

        let listed = tool.execute(&context, &json!({"action": "list"})).unwrap();
        assert!(
            listed.output.contains("write the tests"),
            "{}",
            listed.output
        );
        assert!(
            listed.output.contains("ship the release"),
            "{}",
            listed.output
        );
        assert!(listed.output.contains("\"t1\""), "{}", listed.output);

        tool.execute(
            &context,
            &json!({"action": "update", "id": "t1", "title": "write more tests"}),
        )
        .unwrap();
        let completed = tool
            .execute(&context, &json!({"action": "complete", "id": "t2"}))
            .unwrap();
        assert!(completed.summary.contains("t2"), "{}", completed.summary);

        let listed = tool.execute(&context, &json!({"action": "list"})).unwrap();
        assert!(
            listed.output.contains("write more tests"),
            "{}",
            listed.output
        );
        assert!(listed.output.contains("\"completed\""), "{}", listed.output);

        let store = temp.path().join(".kl/todos.json");
        assert!(store.is_file());
    }

    #[test]
    fn invalid_arguments_are_rejected() {
        let (_temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = TodoTool;

        for args in [
            json!({"action": "nope"}),
            json!({"action": "add", "title": "  "}),
            json!({"action": "add"}),
            json!({"action": "update", "id": "t1"}),
            json!({"action": "update", "id": "t1", "status": "done"}),
            json!({"action": "complete"}),
        ] {
            let error = tool.execute(&context, &args).unwrap_err();
            assert!(
                matches!(error, ToolError::InvalidArgs(_)),
                "{args}: {error}"
            );
        }
    }

    #[test]
    fn unknown_id_is_not_found() {
        let (_temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = TodoTool;

        for args in [
            json!({"action": "complete", "id": "t9"}),
            json!({"action": "update", "id": "t9", "title": "x"}),
        ] {
            let error = tool.execute(&context, &args).unwrap_err();
            assert!(matches!(error, ToolError::NotFound(_)), "{args}: {error}");
        }
    }

    #[test]
    fn oversized_store_is_rejected() {
        let (temp, policy, cancel) = setup();
        let context = context(&policy, &cancel);
        let tool = TodoTool;

        let store = temp.path().join(".kl/todos.json");
        fs::create_dir_all(store.parent().unwrap()).unwrap();
        fs::write(&store, "x".repeat(MAX_TODO_FILE_BYTES + 1)).unwrap();

        let error = tool
            .execute(&context, &json!({"action": "list"}))
            .unwrap_err();
        assert!(matches!(error, ToolError::InvalidArgs(_)), "{error}");
    }

    #[test]
    fn policy_actions_follow_the_action() {
        let tool = TodoTool;
        assert_eq!(
            tool.policy_request(&json!({"action": "list"}))
                .unwrap()
                .action,
            Action::Read
        );
        for args in [
            json!({"action": "add", "title": "x"}),
            json!({"action": "update", "id": "t1", "title": "x"}),
            json!({"action": "complete", "id": "t1"}),
        ] {
            assert_eq!(
                tool.policy_request(&args).unwrap().action,
                Action::Edit,
                "{args}"
            );
            assert!(!tool.read_only(&args), "{args}");
            assert!(!tool.concurrency_safe(&args), "{args}");
        }
        let list = json!({"action": "list"});
        assert!(tool.read_only(&list));
        assert!(tool.concurrency_safe(&list));
    }
}
