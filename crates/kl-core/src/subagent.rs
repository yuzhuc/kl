//! Minimal sequential sub-agent tool. Child agents are read-only and cannot
//! call `subagent` again, while their tool calls still use policy and audit.

use kl_agent::{
    Agent, AgentConfig, AgentError, ApprovalGate, ApprovalRequest, EventSink, TurnStatus,
};
use kl_audit::AuditLog;
use kl_policy::{Action, PolicyEngine, ToolAccess};
use kl_protocol::{ApprovalDecision, CoreEvent};
use kl_provider::{ChatMessage, ProviderRouter};
use kl_sandbox::SandboxManager;
use kl_tools::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, ToolRegistry};
use serde_json::{Value, json};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

const MAX_ITERATIONS: usize = 8;

pub struct SubagentTool {
    router: ProviderRouter,
    tools: Arc<ToolRegistry>,
    sandbox: Arc<SandboxManager>,
    audit: Arc<AuditLog>,
    config: AgentConfig,
    next_turn: AtomicU64,
}

impl SubagentTool {
    pub fn new(
        router: ProviderRouter,
        tools: Arc<ToolRegistry>,
        sandbox: Arc<SandboxManager>,
        audit: Arc<AuditLog>,
        config: AgentConfig,
    ) -> Self {
        Self {
            router,
            tools,
            sandbox,
            audit,
            config,
            next_turn: AtomicU64::new(0),
        }
    }

    fn child_registry(&self) -> ToolRegistry {
        let registry = ToolRegistry::empty();
        for tool in self.tools.snapshot() {
            if tool.name() != "subagent" {
                registry.register(tool);
            }
        }
        registry
    }
}

impl Tool for SubagentTool {
    fn name(&self) -> &str {
        "subagent"
    }

    fn description(&self) -> &str {
        "Run a read-only sequential sub-agent for a focused investigation."
    }

    fn parameters(&self) -> Value {
        json!({
            "type": "object",
            "properties": {
                "prompt": {
                    "type": "string",
                    "description": "Task for the read-only sub-agent."
                }
            },
            "required": ["prompt"],
            "additionalProperties": false
        })
    }

    fn policy_request(&self, args: &Value) -> Result<PolicyRequest, ToolError> {
        prompt_arg(args)?;
        Ok(PolicyRequest {
            action: Action::Plugin,
            resource: "subagent".to_owned(),
        })
    }

    fn validate(&self, args: &Value) -> Result<(), ToolError> {
        prompt_arg(args).map(|_| ())
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let prompt = prompt_arg(args)?;
        let (provider, metadata) = self
            .router
            .get(None, None)
            .map_err(|error| ToolError::Execution(error.to_string()))?;
        let policy = PolicyEngine::new(context.policy.workspace(), context.policy.config().clone())
            .map_err(|error| ToolError::Execution(error.to_string()))?;
        let mut config = self.config.clone();
        config.max_iterations = config.max_iterations.clamp(1, MAX_ITERATIONS);
        config.tool_access = ToolAccess::ReadOnly;

        let agent = Agent::new(
            provider,
            metadata,
            Arc::new(self.child_registry()),
            Arc::new(policy),
            self.sandbox.clone(),
            self.audit.clone(),
            config,
        );
        let mut history: Vec<ChatMessage> = Vec::new();
        // shortcut: child cancellation only inherits the current flag; live parent cancellation needs a shared token.
        let cancel = Arc::new(AtomicBool::new(context.cancel.load(Ordering::Relaxed)));
        let turn_id = format!(
            "subagent-{}",
            self.next_turn.fetch_add(1, Ordering::Relaxed)
        );
        let result = agent
            .run_turn(
                "subagent",
                &turn_id,
                prompt,
                &mut history,
                cancel,
                &SilentSink,
                &DenyApprovals,
            )
            .map_err(|error| ToolError::Execution(error.to_string()))?;
        if result.status != TurnStatus::Completed {
            return Err(ToolError::Execution(format!(
                "subagent {}",
                result.status.as_str()
            )));
        }
        Ok(ToolOutcome::ok("subagent completed", result.text))
    }
}

fn prompt_arg(args: &Value) -> Result<&str, ToolError> {
    args.get("prompt")
        .and_then(Value::as_str)
        .ok_or_else(|| ToolError::InvalidArgs("`prompt` must be a string".to_owned()))
}

struct SilentSink;

impl EventSink for SilentSink {
    fn emit(&self, _event: CoreEvent) -> Result<(), AgentError> {
        Ok(())
    }
}

struct DenyApprovals;

impl ApprovalGate for DenyApprovals {
    fn request(&self, _request: ApprovalRequest) -> ApprovalDecision {
        ApprovalDecision::Deny
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kl_policy::PolicyConfig;
    use kl_provider::{ModelMetadata, Protocol, Provider, ScriptedProvider};
    use kl_tools::ReadFileTool;
    use std::path::Path;
    use std::time::Duration;

    fn tool_with_provider(
        dir: &Path,
        provider: ScriptedProvider,
    ) -> (Arc<SubagentTool>, Arc<AuditLog>, Arc<ToolRegistry>) {
        let metadata = ModelMetadata::new(
            "scripted",
            "scripted",
            Protocol::Scripted,
            "offline://scripted",
            8192,
            0.8,
            4096,
        );
        let router = ProviderRouter::new(
            vec![(metadata, Arc::new(provider) as Arc<dyn Provider>)],
            "scripted",
            "scripted",
        );
        let tools = Arc::new(ToolRegistry::empty());
        tools.register(Arc::new(ReadFileTool));
        let audit = Arc::new(AuditLog::open(dir.join("audit"), 1024 * 1024, 2).unwrap());
        let tool = Arc::new(SubagentTool::new(
            router,
            tools.clone(),
            Arc::new(SandboxManager::policy_only()),
            audit.clone(),
            AgentConfig::default(),
        ));
        tools.register(tool.clone());
        (tool, audit, tools)
    }

    #[test]
    fn prompt_must_be_a_string() {
        let temp = tempfile::tempdir().unwrap();
        let (tool, _, _) = tool_with_provider(temp.path(), ScriptedProvider::empty("scripted"));

        assert!(matches!(
            tool.validate(&json!({})),
            Err(ToolError::InvalidArgs(_))
        ));
        assert!(matches!(
            tool.validate(&json!({"prompt": 1})),
            Err(ToolError::InvalidArgs(_))
        ));
        assert!(matches!(
            tool.policy_request(&json!({})),
            Err(ToolError::InvalidArgs(_))
        ));
        assert!(tool.validate(&json!({"prompt": "inspect"})).is_ok());
        let request = tool.policy_request(&json!({"prompt": "inspect"})).unwrap();
        assert_eq!(request.action, Action::Plugin);
        assert_eq!(request.resource, "subagent");
    }

    #[test]
    fn child_registry_excludes_subagent_itself() {
        let temp = tempfile::tempdir().unwrap();
        let (tool, _, _) = tool_with_provider(temp.path(), ScriptedProvider::empty("scripted"));
        let names = tool.child_registry().names();

        assert!(names.iter().any(|name| name == "read_file"));
        assert!(!names.iter().any(|name| name == "subagent"));
    }

    #[test]
    fn child_runs_read_only_and_records_child_audit() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::write(temp.path().join("note.txt"), "hello from child\n").unwrap();
        let provider = ScriptedProvider::from_json(
            "scripted",
            r#"[{"tool_calls":[{"id":"child-read","name":"read_file","arguments":{"path":"note.txt"}}]},{"text":"child answer"}]"#,
        )
        .unwrap();
        let (tool, audit, _) = tool_with_provider(temp.path(), provider);
        let policy = PolicyEngine::new(temp.path(), PolicyConfig::default()).unwrap();
        let cancel = AtomicBool::new(false);
        let context = ToolContext {
            policy: &policy,
            cancel: &cancel,
            sandbox: Arc::new(SandboxManager::policy_only()),
            timeout: Duration::from_secs(20),
            max_output_bytes: 64 * 1024,
        };

        let outcome = tool
            .execute(&context, &json!({"prompt": "inspect note.txt"}))
            .unwrap();
        assert_eq!(outcome.summary, "subagent completed");
        assert!(outcome.output.contains("child answer"));
        assert_eq!(audit.count().unwrap(), 1);

        let records = audit.tail(1).unwrap();
        assert_eq!(records[0]["session_id"].as_str(), Some("subagent"));
        assert_eq!(records[0]["tool"].as_str(), Some("read_file"));
        assert_eq!(records[0]["ok"].as_bool(), Some(true));
    }
}
