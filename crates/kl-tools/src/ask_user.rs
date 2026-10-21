use crate::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, string_arg};
use kl_policy::Action;
use serde_json::{Value, json};

pub struct AskUserTool;

impl Tool for AskUserTool {
    fn name(&self) -> &str {
        "ask_user"
    }

    fn description(&self) -> &str {
        "Ask the user a yes/no question through the approval channel."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "question": {
                    "type": "string",
                    "description": "Question to show the user"
                },
                "options": {
                    "type": "array",
                    "items": { "type": "string" },
                    "description": "Optional answer hints; the MVP resolves as yes/no"
                }
            },
            "required": ["question"],
            "additionalProperties": false
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        let question = string_arg(args, "question")?.trim();
        if question.is_empty() {
            return Err(ToolError::InvalidArgs(
                "`question` must not be empty".to_owned(),
            ));
        }
        if let Some(options) = args.get("options") {
            let Some(values) = options.as_array() else {
                return Err(ToolError::InvalidArgs(
                    "`options` must be an array".to_owned(),
                ));
            };
            if values.len() > 8 || values.iter().any(|value| value.as_str().is_none()) {
                return Err(ToolError::InvalidArgs(
                    "`options` must contain at most 8 strings".to_owned(),
                ));
            }
        }
        Ok(())
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        string_arg(args, "question")?;
        Ok(PolicyRequest {
            action: Action::Edit,
            resource: ".kl/ask_user".to_owned(),
        })
    }

    fn always_ask(&self) -> bool {
        true
    }

    fn execute(&self, _context: &ToolContext<'_>, _args: &Value) -> Result<ToolOutcome, ToolError> {
        Ok(ToolOutcome::ok("user approved the question", "yes"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::PolicyConfig;
    use std::sync::Arc;
    use std::sync::atomic::AtomicBool;
    use std::time::Duration;

    #[test]
    fn requires_a_question_and_bounded_options() {
        assert!(
            AskUserTool
                .validate(&json!({"question": "continue?"}))
                .is_ok()
        );
        assert!(AskUserTool.validate(&json!({"question": " "})).is_err());
        assert!(
            AskUserTool
                .validate(&json!({"question": "x", "options": "a"}))
                .is_err()
        );
        assert!(
            AskUserTool
                .validate(&json!({
                    "question": "x",
                    "options": ["a", "b", "c", "d", "e", "f", "g", "h", "i"]
                }))
                .is_err()
        );
    }

    #[test]
    fn approval_is_always_required_and_allowed_execution_is_yes() {
        let temp = tempfile::tempdir().unwrap();
        let policy = kl_policy::PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let context = ToolContext {
            policy: &policy,
            cancel: &cancel,
            sandbox: Arc::new(kl_sandbox::SandboxManager::policy_only()),
            timeout: Duration::from_secs(1),
            max_output_bytes: 1024,
        };
        let result = AskUserTool
            .execute(&context, &json!({"question": "continue?"}))
            .unwrap();
        assert!(result.success);
        assert_eq!(result.output, "yes");
        assert!(AskUserTool.always_ask());
    }
}
