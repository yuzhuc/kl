use crate::{PluginError, PluginHost};
use kl_policy::Action;
use kl_provider::{
    CancellationToken, ChatEvent, ChatOutcome, ChatRequest, ModelInfo, Provider, ProviderError,
    ProviderErrorKind,
};
use kl_tools::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome};
use serde_json::Value;
use std::sync::Arc;

pub struct SidecarProvider {
    host: Arc<PluginHost>,
    id: String,
}

impl SidecarProvider {
    pub fn new(host: Arc<PluginHost>, id: impl Into<String>) -> Self {
        Self {
            host,
            id: id.into(),
        }
    }
}

impl Provider for SidecarProvider {
    fn id(&self) -> &str {
        &self.id
    }

    fn kind(&self) -> &str {
        "plugin-sidecar"
    }

    fn models(&self) -> Vec<ModelInfo> {
        self.host
            .provider_models(&self.id)
            .into_iter()
            .map(ModelInfo::new)
            .collect()
    }

    fn available(&self) -> (bool, String) {
        self.host.provider_available(&self.id)
    }

    fn stream_chat(
        &self,
        request: &ChatRequest,
        cancel: &CancellationToken,
        on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        if cancel.is_cancelled() {
            return Err(ProviderError::cancelled());
        }
        let outcome = self
            .host
            .provider_chat(&self.id, request)
            .map_err(provider_error)?;
        for chunk in chunk_text(&outcome.text, 64) {
            if cancel.is_cancelled() {
                return Err(ProviderError::cancelled());
            }
            on_event(ChatEvent::TextDelta(chunk));
        }
        Ok(outcome)
    }
}

pub struct SidecarTool {
    host: Arc<PluginHost>,
    plugin_id: String,
    name: String,
    description: String,
    parameters: Value,
}

impl SidecarTool {
    pub fn new(host: Arc<PluginHost>, plugin_id: impl Into<String>) -> Option<Self> {
        let plugin_id = plugin_id.into();
        let manifest = host.manifest(&plugin_id)?;
        Some(Self {
            host,
            plugin_id,
            name: manifest.tool_name(),
            description: format!(
                "Sidecar plugin tool `{}` from plugin `{}` v{}",
                manifest.tool_name(),
                manifest.id,
                manifest.version
            ),
            parameters: manifest
                .tool_parameters
                .unwrap_or_else(|| serde_json::json!({"type": "object"})),
        })
    }
}

impl Tool for SidecarTool {
    fn name(&self) -> &str {
        &self.name
    }

    fn description(&self) -> &str {
        &self.description
    }

    fn parameters(&self) -> Value {
        self.parameters.clone()
    }

    fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Plugin,
            resource: format!("plugin:{}", self.plugin_id),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        context
            .sandbox
            .ensure_process_execution()
            .map_err(|error| ToolError::Policy(error.to_string()))?;
        self.host
            .tool_execute(
                &self.plugin_id,
                &self.name,
                args,
                context.policy.workspace(),
            )
            .map_err(tool_error)
    }
}

fn provider_error(error: PluginError) -> ProviderError {
    let kind = match &error {
        PluginError::PermissionDenied(_)
        | PluginError::Disabled(_)
        | PluginError::KindMismatch(_)
        | PluginError::NotFound(_) => ProviderErrorKind::Config,
        PluginError::Sidecar(sidecar) => match sidecar {
            crate::SidecarError::Timeout(_) => ProviderErrorKind::Timeout,
            crate::SidecarError::Rpc { .. } => ProviderErrorKind::Server,
            _ => ProviderErrorKind::Transport,
        },
        PluginError::Manifest(_) | PluginError::Io(_) => ProviderErrorKind::Config,
        _ => ProviderErrorKind::Server,
    };
    ProviderError::new(kind, error.to_string())
}

fn tool_error(error: PluginError) -> ToolError {
    match error {
        PluginError::PermissionDenied(message) => ToolError::Policy(message),
        PluginError::Sandbox(violation) => ToolError::Sandbox(violation),
        other => ToolError::Execution(other.to_string()),
    }
}

fn chunk_text(text: &str, size: usize) -> Vec<String> {
    if text.is_empty() {
        return Vec::new();
    }
    let characters: Vec<char> = text.chars().collect();
    characters
        .chunks(size)
        .map(|chunk| chunk.iter().collect())
        .collect()
}
