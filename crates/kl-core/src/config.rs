use kl_policy::{PolicyConfig, ToolAccess};
use kl_protocol::{
    AgentConfigSetParams, ApprovalMode, DefaultModelResult, EmojiUsage, HeadingStyle,
    OutputVerbosity, PersonalStyle, ProviderDeleteResult, ProviderEnabledResult, ProviderKeyResult,
    ProviderProtocolUrls, ProviderSetApiKeyParams, ProviderSetEnabledParams, ProviderUpsertParams,
    ProviderUpsertResult, ReasoningEffort, ReasoningSummary, SetDefaultModelParams, ToneLevel,
    VisualAnswers,
};
use kl_provider::{ModelMetadata, Protocol};
use kl_sandbox::SandboxConfig;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct Config {
    pub agent: AgentSettings,
    pub policy: PolicyConfig,
    pub sandbox: SandboxConfig,
    pub audit: AuditConfig,
    pub providers: BTreeMap<String, ProviderEntry>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct ProviderEntry {
    #[serde(default = "default_true")]
    pub enabled: bool,
    pub path: String,
}

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(default)]
pub struct AuditConfig {
    pub rotate_bytes: u64,
    pub keep_files: usize,
}

impl Default for AuditConfig {
    fn default() -> Self {
        Self {
            rotate_bytes: 10 * 1024 * 1024,
            keep_files: 5,
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum QualityMode {
    Fast,
    #[default]
    Balanced,
    Deep,
}

impl QualityMode {
    pub fn parse(value: &str) -> Option<Self> {
        match value.to_ascii_lowercase().as_str() {
            "fast" => Some(Self::Fast),
            "balanced" => Some(Self::Balanced),
            "deep" => Some(Self::Deep),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Fast => "fast",
            Self::Balanced => "balanced",
            Self::Deep => "deep",
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum AgentPreset {
    #[default]
    Build,
    Plan,
    Explore,
    Review,
    Verify,
    Deep,
}

impl AgentPreset {
    pub fn parse(value: &str) -> Option<Self> {
        let value = value.to_ascii_lowercase();
        [
            Self::Build,
            Self::Plan,
            Self::Explore,
            Self::Review,
            Self::Verify,
            Self::Deep,
        ]
        .into_iter()
        .find(|preset| preset.as_str() == value)
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Build => "build",
            Self::Plan => "plan",
            Self::Explore => "explore",
            Self::Review => "review",
            Self::Verify => "verify",
            Self::Deep => "deep",
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::Build => "Build",
            Self::Plan => "Plan",
            Self::Explore => "Explore",
            Self::Review => "Review",
            Self::Verify => "Verify",
            Self::Deep => "Deep",
        }
    }

    pub fn description(self) -> &'static str {
        match self {
            Self::Build => "Implements tasks with full tool access.",
            Self::Plan => "Plans work without changing files.",
            Self::Explore => "Explores the workspace and reports findings.",
            Self::Review => "Reviews the current changes and reports issues.",
            Self::Verify => "Runs non-mutating checks and reports results.",
            Self::Deep => "Runs the staged deep pipeline.",
        }
    }

    pub fn tool_access(self) -> ToolAccess {
        match self {
            Self::Build | Self::Deep => ToolAccess::Full,
            Self::Plan | Self::Explore | Self::Review => ToolAccess::ReadOnly,
            Self::Verify => ToolAccess::VerifyOnly,
        }
    }

    pub fn prompt_prefix(self) -> &'static str {
        match self {
            Self::Build | Self::Deep => "",
            Self::Plan => {
                "You are in plan mode. Inspect and reason without modifying files. Produce a concrete plan.\n\n"
            }
            Self::Explore => {
                "You are in explore mode. Inspect the workspace without modifying files and report findings.\n\n"
            }
            Self::Review => {
                "You are in review mode. Inspect without modifying files and report concrete issues with evidence.\n\n"
            }
            Self::Verify => {
                "You are in verify mode. Run non-mutating checks and report results without editing files.\n\n"
            }
        }
    }
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(default)]
pub struct RoleSettings {
    pub provider: Option<String>,
    pub model: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct AgentSettings {
    pub provider: String,
    pub model: String,
    /// Offline-only scripted provider source. Never used by native adapters.
    pub scripted_path: Option<String>,
    /// Keep the scripted cursor across evaluation turns; used by offline demos.
    pub scripted_sequential: bool,
    pub quality: QualityMode,
    pub approval_mode: ApprovalMode,
    pub reasoning_effort: ReasoningEffort,
    pub web_search: bool,
    pub network_access: bool,
    pub output_verbosity: OutputVerbosity,
    pub reasoning_summary: ReasoningSummary,
    pub memory_enabled: bool,
    pub plugins_enabled: bool,
    pub custom_instructions: String,
    pub personal_style: PersonalStyle,
    pub warmth: ToneLevel,
    pub enthusiasm: ToneLevel,
    pub emoji_usage: EmojiUsage,
    pub visual_answers: VisualAnswers,
    pub heading_style: HeadingStyle,
    pub quick_answers: bool,
    pub roles: BTreeMap<String, RoleSettings>,
    pub max_iterations: usize,
    pub max_tool_output_bytes: usize,
    pub command_timeout_secs: u64,
    pub approval_timeout_secs: u64,
    pub review_timeout_secs: u64,
    pub deep_max_stages: usize,
    pub deep_max_tool_calls: usize,
    pub deep_token_budget: u64,
    pub deep_timeout_secs: u64,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ProviderFile {
    #[serde(default)]
    pub api_key_env: Option<String>,
    #[serde(default)]
    pub api_key: Option<String>,
    #[serde(default)]
    pub protocols: ProtocolUrls,
    #[serde(default)]
    pub models: Vec<ModelConfig>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default)]
pub struct ProtocolUrls {
    #[serde(rename = "openai-chat")]
    pub openai_chat: Option<String>,
    pub anthropic: Option<String>,
    #[serde(rename = "openai-responses")]
    pub openai_responses: Option<String>,
}

impl ProtocolUrls {
    pub fn url(&self, protocol: Protocol) -> Option<&str> {
        let url = match protocol {
            Protocol::OpenAiChat => self.openai_chat.as_deref(),
            Protocol::Anthropic => self.anthropic.as_deref(),
            Protocol::OpenAiResponses => self.openai_responses.as_deref(),
            Protocol::Scripted | Protocol::Plugin => None,
        };
        url.filter(|url| !url.trim().is_empty())
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct ModelConfig {
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
    pub protocol: Protocol,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    /// Optional per-model override; when present it is the complete request URL.
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub reasoning_effort: Option<ReasoningEffort>,
    #[serde(default)]
    pub reasoning_efforts: Vec<ReasoningEffort>,
}

#[derive(Debug, Clone)]
pub struct LoadedProvider {
    pub id: String,
    pub path: PathBuf,
    pub api_key: Option<String>,
    pub api_key_env: Option<String>,
    pub protocols: ProviderProtocolUrls,
    pub models: Vec<ModelMetadata>,
}

#[derive(Debug, Clone, Default)]
pub struct ProviderCatalog {
    pub providers: Vec<LoadedProvider>,
    pub disabled: HashSet<String>,
}

/// One configured protocol plus the `/models` candidates for that endpoint.
#[derive(Debug, Clone)]
pub struct ProviderModelEndpoint {
    pub protocol: Protocol,
    pub chat_url: String,
    pub models_urls: Vec<String>,
    pub api_key: Option<String>,
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("cannot read config: {0}")]
    Io(#[from] std::io::Error),
    #[error("cannot parse config {path}: {message}")]
    Parse { path: String, message: String },
    #[error("default provider `{0}` is not configured")]
    MissingDefaultProvider(String),
    #[error("provider `{0}` is enabled but has no path")]
    MissingProviderPath(String),
    #[error("provider `{provider}` is enabled but file is missing: {path}")]
    ProviderFileMissing { provider: String, path: String },
    #[error("cannot read provider `{provider}` file {path}: {message}")]
    ProviderFileIo {
        provider: String,
        path: String,
        message: String,
    },
    #[error("cannot parse provider `{provider}` file {path}: {message}")]
    ProviderFileParse {
        provider: String,
        path: String,
        message: String,
    },
    #[error("provider `{0}` must not set both api_key_env and api_key")]
    InvalidCredentials(String),
    #[error("unknown provider `{0}`")]
    UnknownProvider(String),
    #[error("provider `{0}` has no usable protocol URL")]
    NoProviderUrl(String),
    #[error("provider `{0}` is disabled")]
    ProviderDisabled(String),
    #[error("provider `{provider}` has no model `{model}`")]
    UnknownModel { provider: String, model: String },
    #[error("model `{model}` for provider `{provider}` is disabled")]
    ModelDisabled { provider: String, model: String },
    #[error("invalid provider id `{0}`")]
    InvalidProviderId(String),
    #[error("API key must not be empty")]
    EmptyApiKey,
    #[error("cannot serialize TOML: {0}")]
    SerializeToml(String),
    #[error("provider `{provider}` has duplicate model id `{model}`")]
    DuplicateModel { provider: String, model: String },
    #[error("provider `{provider}` model `{model}` uses unsupported protocol `{protocol}`")]
    UnsupportedProtocol {
        provider: String,
        model: String,
        protocol: String,
    },
    #[error("provider `{provider}` model `{model}` has invalid context_window {value}")]
    InvalidContextWindow {
        provider: String,
        model: String,
        value: u32,
    },
    #[error(
        "provider `{provider}` model `{model}` has invalid compact_threshold {value}; expected 0.0-1.0"
    )]
    InvalidCompactThreshold {
        provider: String,
        model: String,
        value: f32,
    },
    #[error("provider `{provider}` model `{model}` has invalid max_output_tokens {value}")]
    InvalidMaxOutput {
        provider: String,
        model: String,
        value: u32,
    },
    #[error("provider `{provider}` model `{model}` protocol `{protocol}` has no complete URL")]
    MissingProtocolUrl {
        provider: String,
        model: String,
        protocol: String,
    },
    #[error("provider `{provider}` model `{model}` URL is not a complete http(s) endpoint: {url}")]
    InvalidUrl {
        provider: String,
        model: String,
        url: String,
    },
    #[error("scripted provider file is missing: {0}")]
    ScriptedFileMissing(String),
    #[error("cannot load scripted provider: {0}")]
    Scripted(String),
}

impl Default for AgentSettings {
    fn default() -> Self {
        Self {
            provider: String::new(),
            model: String::new(),
            scripted_path: None,
            scripted_sequential: false,
            quality: QualityMode::default(),
            approval_mode: ApprovalMode::default(),
            reasoning_effort: ReasoningEffort::default(),
            web_search: true,
            network_access: true,
            output_verbosity: OutputVerbosity::default(),
            reasoning_summary: ReasoningSummary::default(),
            memory_enabled: true,
            plugins_enabled: true,
            custom_instructions: String::new(),
            personal_style: PersonalStyle::default(),
            warmth: ToneLevel::default(),
            enthusiasm: ToneLevel::default(),
            emoji_usage: EmojiUsage::default(),
            visual_answers: VisualAnswers::default(),
            heading_style: HeadingStyle::default(),
            quick_answers: true,
            roles: BTreeMap::new(),
            max_iterations: 12,
            max_tool_output_bytes: 32 * 1024,
            command_timeout_secs: 120,
            approval_timeout_secs: 180,
            review_timeout_secs: 60,
            deep_max_stages: 5,
            deep_max_tool_calls: 48,
            deep_token_budget: 120_000,
            deep_timeout_secs: 900,
        }
    }
}

impl Config {
    pub fn load(path: &Path) -> Result<Self, ConfigError> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let text = std::fs::read_to_string(path)?;
        let config: Self = toml::from_str(&text).map_err(|error| ConfigError::Parse {
            path: path.display().to_string(),
            message: redact_api_key(&error.to_string()),
        })?;
        config.validate()?;
        Ok(config)
    }

    pub fn validate(&self) -> Result<(), ConfigError> {
        let scripted_configured =
            self.agent.provider == "scripted" && self.agent.scripted_path.is_some();
        // An empty provider table is a valid GUI-managed state: the user can still
        // start Core and add a new provider. A non-empty table must have a valid default.
        if !self.agent.provider.is_empty()
            && !self.providers.is_empty()
            && !self.providers.contains_key(&self.agent.provider)
            && !scripted_configured
        {
            return Err(ConfigError::MissingDefaultProvider(
                self.agent.provider.clone(),
            ));
        }
        for (id, entry) in &self.providers {
            if entry.enabled && entry.path.trim().is_empty() {
                return Err(ConfigError::MissingProviderPath(id.clone()));
            }
        }
        Ok(())
    }

    pub fn config_dir(path: &Path) -> PathBuf {
        path.parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| PathBuf::from("."))
    }

    /// Global config only; provider files are redacted separately.
    pub fn redacted(&self) -> Value {
        json!({
            "agent": {
                "provider": self.agent.provider,
                "model": self.agent.model,
                "scripted_path": self.agent.scripted_path,
                "scripted_sequential": self.agent.scripted_sequential,
                "quality": self.agent.quality,
                "approval_mode": self.agent.approval_mode,
                "reasoning_effort": self.agent.reasoning_effort,
                "web_search": self.agent.web_search,
                "network_access": self.agent.network_access,
                "output_verbosity": self.agent.output_verbosity,
                "reasoning_summary": self.agent.reasoning_summary,
                "memory_enabled": self.agent.memory_enabled,
                "plugins_enabled": self.agent.plugins_enabled,
                "custom_instructions": self.agent.custom_instructions,
                "personal_style": self.agent.personal_style,
                "warmth": self.agent.warmth,
                "enthusiasm": self.agent.enthusiasm,
                "emoji_usage": self.agent.emoji_usage,
                "visual_answers": self.agent.visual_answers,
                "heading_style": self.agent.heading_style,
                "quick_answers": self.agent.quick_answers,
                "roles": self.agent.roles,
                "max_iterations": self.agent.max_iterations,
                "max_tool_output_bytes": self.agent.max_tool_output_bytes,
                "command_timeout_secs": self.agent.command_timeout_secs,
                "approval_timeout_secs": self.agent.approval_timeout_secs,
                "review_timeout_secs": self.agent.review_timeout_secs,
                "deep_max_stages": self.agent.deep_max_stages,
                "deep_max_tool_calls": self.agent.deep_max_tool_calls,
                "deep_token_budget": self.agent.deep_token_budget,
                "deep_timeout_secs": self.agent.deep_timeout_secs,
            },
            "policy": self.policy,
            "sandbox": self.sandbox,
            "audit": self.audit,
            "providers": self.providers,
        })
    }
}

impl ProviderCatalog {
    pub fn load(config: &Config, config_dir: &Path, home: &Path) -> Result<Self, ConfigError> {
        let mut catalog = Self::default();
        for (id, entry) in &config.providers {
            if !is_valid_provider_id(id) {
                return Err(ConfigError::InvalidProviderId(id.clone()));
            }
            if !entry.enabled {
                // A disabled provider is never loaded, even if its file is
                // missing or invalid.
                catalog.disabled.insert(id.clone());
                continue;
            }
            if entry.path.trim().is_empty() {
                return Err(ConfigError::MissingProviderPath(id.clone()));
            }
            let path = resolve_path(config_dir, &entry.path);
            if !path.exists() {
                return Err(ConfigError::ProviderFileMissing {
                    provider: id.clone(),
                    path: path.display().to_string(),
                });
            }
            let text =
                std::fs::read_to_string(&path).map_err(|error| ConfigError::ProviderFileIo {
                    provider: id.clone(),
                    path: path.display().to_string(),
                    message: error.to_string(),
                })?;
            let file: ProviderFile =
                toml::from_str(&text).map_err(|error| ConfigError::ProviderFileParse {
                    provider: id.clone(),
                    path: path.display().to_string(),
                    message: redact_api_key(&error.to_string()),
                })?;
            validate_provider_file(id, &path, &file)?;

            let env_key = file
                .api_key_env
                .as_ref()
                .and_then(|name| std::env::var(name).ok())
                .filter(|key| !key.trim().is_empty());
            let file_key = read_provider_key(home, id);
            let api_key = env_key
                .or(file_key)
                .or_else(|| file.api_key.clone().filter(|key| !key.trim().is_empty()));
            let mut models = Vec::new();
            for model in &file.models {
                let url = model
                    .url
                    .clone()
                    .filter(|url| !url.trim().is_empty())
                    .or_else(|| file.protocols.url(model.protocol).map(str::to_owned))
                    .unwrap_or_default();
                models.push(
                    ModelMetadata::new(
                        id.clone(),
                        model.id.clone(),
                        model.protocol,
                        url,
                        model.context_window,
                        model.compact_threshold,
                        model.max_output_tokens,
                    )
                    .with_name(model.name.clone().unwrap_or_default())
                    .with_enabled(model.enabled)
                    .with_reasoning_effort(
                        model
                            .reasoning_effort
                            .filter(|effort| *effort != ReasoningEffort::Default)
                            .map(|effort| effort.as_str().to_owned()),
                    )
                    .with_reasoning_efforts(
                        model
                            .reasoning_efforts
                            .iter()
                            .filter(|effort| **effort != ReasoningEffort::Default)
                            .map(|effort| effort.as_str().to_owned())
                            .collect(),
                    ),
                );
            }
            catalog.providers.push(LoadedProvider {
                id: id.clone(),
                path,
                api_key,
                api_key_env: file.api_key_env,
                protocols: ProviderProtocolUrls {
                    openai_chat: file.protocols.openai_chat.clone(),
                    anthropic: file.protocols.anthropic.clone(),
                    openai_responses: file.protocols.openai_responses.clone(),
                },
                models,
            });
        }
        Ok(catalog)
    }

    pub fn redacted(&self) -> Value {
        json!({
            "providers": self.providers.iter().map(|provider| json!({
                "id": provider.id,
                "path": provider.path,
                "enabled": true,
                "api_key_env": provider.api_key_env,
                "key_configured": provider.api_key.is_some(),
                "credential": if provider.api_key.is_some() { "configured" } else { "missing" },
                "models": provider.models,
            })).collect::<Vec<_>>(),
        })
    }
}

/// Resolves protocol endpoints for model discovery, even when the provider is disabled.
pub fn provider_model_endpoints(
    config_path: &Path,
    home: &Path,
    id: &str,
) -> Result<Vec<ProviderModelEndpoint>, ConfigError> {
    if !is_valid_provider_id(id) {
        return Err(ConfigError::InvalidProviderId(id.to_owned()));
    }
    let config = Config::load(config_path)?;
    let entry = config
        .providers
        .get(id)
        .ok_or_else(|| ConfigError::UnknownProvider(id.to_owned()))?;
    let config_dir = Config::config_dir(config_path);
    let stored_path = if entry.path.trim().is_empty() {
        format!("providers/{id}.toml")
    } else {
        entry.path.clone()
    };
    let path = resolve_path(&config_dir, &stored_path);
    if !path.exists() {
        return Err(ConfigError::ProviderFileMissing {
            provider: id.to_owned(),
            path: path.display().to_string(),
        });
    }

    let text = std::fs::read_to_string(&path).map_err(|error| ConfigError::ProviderFileIo {
        provider: id.to_owned(),
        path: path.display().to_string(),
        message: error.to_string(),
    })?;
    let file: ProviderFile =
        toml::from_str(&text).map_err(|error| ConfigError::ProviderFileParse {
            provider: id.to_owned(),
            path: path.display().to_string(),
            message: redact_api_key(&error.to_string()),
        })?;

    let env_key = file
        .api_key_env
        .as_ref()
        .and_then(|name| std::env::var(name).ok())
        .filter(|key| !key.trim().is_empty());
    let api_key = env_key
        .or_else(|| read_provider_key(home, id))
        .or_else(|| file.api_key.clone().filter(|key| !key.trim().is_empty()));

    let mut endpoints = Vec::new();
    for (protocol, url) in [
        (Protocol::OpenAiChat, file.protocols.openai_chat.as_deref()),
        (Protocol::Anthropic, file.protocols.anthropic.as_deref()),
        (
            Protocol::OpenAiResponses,
            file.protocols.openai_responses.as_deref(),
        ),
    ] {
        let Some(url) = url.map(str::trim).filter(|url| !url.is_empty()) else {
            continue;
        };
        if !is_complete_url(url) {
            continue;
        }
        let base = model_list_base(url);
        if base.is_empty() {
            continue;
        }
        endpoints.push(ProviderModelEndpoint {
            protocol,
            chat_url: url.to_owned(),
            models_urls: vec![format!("{base}/models"), format!("{base}/modul")],
            api_key: api_key.clone(),
        });
    }

    if endpoints.is_empty() {
        return Err(ConfigError::NoProviderUrl(id.to_owned()));
    }
    Ok(endpoints)
}

fn model_list_base(endpoint: &str) -> String {
    let url = endpoint
        .split(['?', '#'])
        .next()
        .unwrap_or(endpoint)
        .trim_end_matches('/');
    for suffix in ["/chat/completions", "/messages", "/responses"] {
        if let Some(base) = url.strip_suffix(suffix) {
            return base.trim_end_matches('/').to_owned();
        }
    }
    url.to_owned()
}

fn validate_provider_file(id: &str, path: &Path, file: &ProviderFile) -> Result<(), ConfigError> {
    let has_inline = file
        .api_key
        .as_deref()
        .is_some_and(|value| !value.trim().is_empty());
    let has_env = file
        .api_key_env
        .as_deref()
        .is_some_and(|value| !value.trim().is_empty());
    if has_inline && has_env {
        return Err(ConfigError::InvalidCredentials(id.to_owned()));
    }
    let mut seen = HashSet::new();
    for model in &file.models {
        if !seen.insert(model.id.as_str()) {
            return Err(ConfigError::DuplicateModel {
                provider: id.to_owned(),
                model: model.id.clone(),
            });
        }
    }
    for model in &file.models {
        if !model.protocol.is_native() {
            return Err(ConfigError::UnsupportedProtocol {
                provider: id.to_owned(),
                model: model.id.clone(),
                protocol: model.protocol.as_str().to_owned(),
            });
        }
        if model.context_window == 0 {
            return Err(ConfigError::InvalidContextWindow {
                provider: id.to_owned(),
                model: model.id.clone(),
                value: model.context_window,
            });
        }
        if !model.compact_threshold.is_finite() || !(0.0..=1.0).contains(&model.compact_threshold) {
            return Err(ConfigError::InvalidCompactThreshold {
                provider: id.to_owned(),
                model: model.id.clone(),
                value: model.compact_threshold,
            });
        }
        if model.max_output_tokens == 0 {
            return Err(ConfigError::InvalidMaxOutput {
                provider: id.to_owned(),
                model: model.id.clone(),
                value: model.max_output_tokens,
            });
        }
        let url = model
            .url
            .as_deref()
            .filter(|url| !url.trim().is_empty())
            .or_else(|| file.protocols.url(model.protocol));
        let Some(url) = url else {
            return Err(ConfigError::MissingProtocolUrl {
                provider: id.to_owned(),
                model: model.id.clone(),
                protocol: model.protocol.as_str().to_owned(),
            });
        };
        if !is_complete_url(url) {
            return Err(ConfigError::InvalidUrl {
                provider: id.to_owned(),
                model: model.id.clone(),
                url: url.to_owned(),
            });
        }
    }

    // Make the manifest path visible in the error type even when validation
    // succeeds; this keeps call sites from accidentally dropping context.
    let _ = path;
    Ok(())
}

fn redact_api_key(message: &str) -> String {
    message
        .lines()
        .map(|line| {
            let lower = line.to_ascii_lowercase();
            if lower.contains("api_key") && !lower.contains("api_key_env") {
                if let Some(index) = line.find('=') {
                    return format!("{} \"<redacted>\"", &line[..=index]);
                }
            }
            line.to_owned()
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn is_complete_url(url: &str) -> bool {
    let url = url.trim();
    let Some(rest) = url
        .strip_prefix("http://")
        .or_else(|| url.strip_prefix("https://"))
    else {
        return false;
    };
    let authority = rest.split(['/', '?', '#']).next().unwrap_or_default();
    !authority.is_empty() && !authority.starts_with(':') && !url.chars().any(char::is_whitespace)
}

fn resolve_path(config_dir: &Path, path: &str) -> PathBuf {
    let path = Path::new(path);
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        config_dir.join(path)
    }
}

fn is_valid_provider_id(id: &str) -> bool {
    !id.is_empty()
        && id != "."
        && id != ".."
        && id.chars().all(|character| {
            character.is_ascii_alphanumeric() || matches!(character, '-' | '_' | '.')
        })
}

fn read_provider_key(home: &Path, id: &str) -> Option<String> {
    if !is_valid_provider_id(id) {
        return None;
    }
    std::fs::read_to_string(home.join("credentials").join(format!("{id}.key")))
        .ok()
        .map(|key| key.trim().to_owned())
        .filter(|key| !key.is_empty())
}

fn read_toml_value(path: &Path) -> Result<toml::Value, ConfigError> {
    if !path.exists() {
        return Ok(toml::Value::Table(Default::default()));
    }
    let text = std::fs::read_to_string(path)?;
    toml::from_str(&text).map_err(|error| ConfigError::Parse {
        path: path.display().to_string(),
        message: redact_api_key(&error.to_string()),
    })
}

fn write_toml_atomic(path: &Path, value: &toml::Value) -> Result<(), ConfigError> {
    let text = toml::to_string_pretty(value)
        .map_err(|error| ConfigError::SerializeToml(error.to_string()))?;
    write_bytes_atomic(path, text.as_bytes(), false)
}

fn write_bytes_atomic(path: &Path, bytes: &[u8], private: bool) -> Result<(), ConfigError> {
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    std::fs::create_dir_all(parent)?;
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("kl");
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temporary = path.with_file_name(format!(".{name}.{}.{stamp}.tmp", std::process::id()));
    if let Err(error) = std::fs::write(&temporary, bytes) {
        let _ = std::fs::remove_file(&temporary);
        return Err(ConfigError::Io(error));
    }
    #[cfg(unix)]
    if private {
        use std::os::unix::fs::PermissionsExt;
        if let Err(error) =
            std::fs::set_permissions(&temporary, std::fs::Permissions::from_mode(0o600))
        {
            let _ = std::fs::remove_file(&temporary);
            return Err(ConfigError::Io(error));
        }
    }
    #[cfg(not(unix))]
    let _ = private;
    if let Err(error) = std::fs::rename(&temporary, path) {
        let _ = std::fs::remove_file(&temporary);
        return Err(ConfigError::Io(error));
    }
    Ok(())
}

fn ensure_table<'a>(
    value: &'a mut toml::Value,
    key: &str,
) -> Result<&'a mut toml::map::Map<String, toml::Value>, ConfigError> {
    let table = value.as_table_mut().ok_or_else(|| {
        ConfigError::SerializeToml("configuration root must be a table".to_owned())
    })?;
    let entry = table
        .entry(key.to_owned())
        .or_insert_with(|| toml::Value::Table(Default::default()));
    entry
        .as_table_mut()
        .ok_or_else(|| ConfigError::SerializeToml(format!("`{key}` must be a table")))
}

fn provider_entry_mut<'a>(
    config: &'a mut toml::Value,
    id: &str,
) -> Result<&'a mut toml::map::Map<String, toml::Value>, ConfigError> {
    let providers = ensure_table(config, "providers")?;
    let entry = providers
        .get_mut(id)
        .ok_or_else(|| ConfigError::UnknownProvider(id.to_owned()))?;
    entry
        .as_table_mut()
        .ok_or_else(|| ConfigError::SerializeToml(format!("`providers.{id}` must be a table")))
}

fn parse_protocol(value: &str) -> Option<Protocol> {
    match value {
        "openai-chat" => Some(Protocol::OpenAiChat),
        "anthropic" => Some(Protocol::Anthropic),
        "openai-responses" => Some(Protocol::OpenAiResponses),
        _ => None,
    }
}

fn clean(value: Option<String>) -> Option<String> {
    value
        .map(|value| value.trim().to_owned())
        .filter(|value| !value.is_empty())
}

fn compact_f64(value: f32) -> f64 {
    value.to_string().parse::<f64>().unwrap_or(value as f64)
}

pub fn provider_upsert(
    config_path: &Path,
    params: &ProviderUpsertParams,
) -> Result<ProviderUpsertResult, ConfigError> {
    if !is_valid_provider_id(&params.id) {
        return Err(ConfigError::InvalidProviderId(params.id.clone()));
    }
    let config_dir = Config::config_dir(config_path);
    let mut config_value = read_toml_value(config_path)?;
    let default_missing = {
        let current = config_value
            .get("agent")
            .and_then(toml::Value::as_table)
            .and_then(|agent| agent.get("provider"))
            .and_then(toml::Value::as_str)
            .unwrap_or_default();
        current.is_empty()
            || !config_value
                .get("providers")
                .and_then(toml::Value::as_table)
                .is_some_and(|providers| providers.contains_key(current))
    };
    let stored_path = {
        let providers = ensure_table(&mut config_value, "providers")?;
        let entry = providers
            .entry(params.id.clone())
            .or_insert_with(|| toml::Value::Table(Default::default()));
        let table = entry.as_table_mut().ok_or_else(|| {
            ConfigError::SerializeToml(format!("`providers.{}` must be a table", params.id))
        })?;
        table
            .get("path")
            .and_then(toml::Value::as_str)
            .filter(|path| !path.trim().is_empty())
            .map(str::to_owned)
            .unwrap_or_else(|| format!("providers/{}.toml", params.id))
    };
    let provider_path = resolve_path(&config_dir, &stored_path);

    let protocols = ProtocolUrls {
        openai_chat: clean(params.protocols.openai_chat.clone()),
        anthropic: clean(params.protocols.anthropic.clone()),
        openai_responses: clean(params.protocols.openai_responses.clone()),
    };
    let models = params
        .models
        .iter()
        .map(|model| {
            let protocol = parse_protocol(&model.protocol).ok_or_else(|| {
                ConfigError::UnsupportedProtocol {
                    provider: params.id.clone(),
                    model: model.id.clone(),
                    protocol: model.protocol.clone(),
                }
            })?;
            Ok(ModelConfig {
                id: model.id.clone(),
                name: clean(model.name.clone()),
                protocol,
                context_window: model.context_window,
                compact_threshold: model.compact_threshold,
                max_output_tokens: model.max_output_tokens,
                url: clean(model.url.clone()),
                enabled: model.enabled,
                reasoning_effort: (model.reasoning_effort != ReasoningEffort::Default)
                    .then_some(model.reasoning_effort),
                reasoning_efforts: model.reasoning_efforts.clone(),
            })
        })
        .collect::<Result<Vec<_>, ConfigError>>()?;
    let api_key_env = clean(params.api_key_env.clone());
    let file = ProviderFile {
        api_key_env: api_key_env.clone(),
        api_key: None,
        protocols,
        models,
    };
    validate_provider_file(&params.id, &provider_path, &file)?;

    let mut provider_value = read_toml_value(&provider_path)?;
    let existing_models = provider_value
        .get("models")
        .and_then(toml::Value::as_array)
        .cloned()
        .unwrap_or_default();
    let existing_by_id = existing_models
        .iter()
        .filter_map(|model| {
            let table = model.as_table()?;
            let id = table.get("id")?.as_str()?.to_owned();
            Some((id, table.clone()))
        })
        .collect::<HashMap<_, _>>();
    {
        let table = provider_value.as_table_mut().ok_or_else(|| {
            ConfigError::SerializeToml("provider file root must be a table".to_owned())
        })?;
        if api_key_env.is_some() {
            table.remove("api_key");
        }
        let models_value = file
            .models
            .iter()
            .map(|model| {
                let mut model_table = existing_by_id.get(&model.id).cloned().unwrap_or_default();
                model_table.insert("id".to_owned(), toml::Value::String(model.id.clone()));
                model_table.insert(
                    "protocol".to_owned(),
                    toml::Value::String(model.protocol.as_str().to_owned()),
                );
                model_table.insert(
                    "context_window".to_owned(),
                    toml::Value::Integer(i64::from(model.context_window)),
                );
                model_table.insert(
                    "compact_threshold".to_owned(),
                    toml::Value::Float(compact_f64(model.compact_threshold)),
                );
                model_table.insert(
                    "max_output_tokens".to_owned(),
                    toml::Value::Integer(i64::from(model.max_output_tokens)),
                );
                if let Some(name) = &model.name {
                    model_table.insert("name".to_owned(), toml::Value::String(name.clone()));
                } else {
                    model_table.remove("name");
                }
                if let Some(url) = &model.url {
                    model_table.insert("url".to_owned(), toml::Value::String(url.clone()));
                } else {
                    model_table.remove("url");
                }
                model_table.insert("enabled".to_owned(), toml::Value::Boolean(model.enabled));
                match &model.reasoning_effort {
                    Some(effort) => {
                        model_table.insert(
                            "reasoning_effort".to_owned(),
                            toml::Value::String(effort.as_str().to_owned()),
                        );
                    }
                    None => {
                        model_table.remove("reasoning_effort");
                    }
                }
                if model.reasoning_efforts.is_empty() {
                    model_table.remove("reasoning_efforts");
                } else {
                    model_table.insert(
                        "reasoning_efforts".to_owned(),
                        toml::Value::Array(
                            model
                                .reasoning_efforts
                                .iter()
                                .map(|effort| toml::Value::String(effort.as_str().to_owned()))
                                .collect(),
                        ),
                    );
                }
                toml::Value::Table(model_table)
            })
            .collect::<Vec<_>>();
        table.insert("models".to_owned(), toml::Value::Array(models_value));
        match &params.api_key_env {
            Some(value) if value.trim().is_empty() => {
                table.remove("api_key_env");
            }
            Some(value) => {
                table.insert(
                    "api_key_env".to_owned(),
                    toml::Value::String(value.trim().to_owned()),
                );
            }
            None => {}
        }
    }
    {
        let protocols = ensure_table(&mut provider_value, "protocols")?;
        for (name, url) in [
            ("openai-chat", &file.protocols.openai_chat),
            ("anthropic", &file.protocols.anthropic),
            ("openai-responses", &file.protocols.openai_responses),
        ] {
            if let Some(url) = url {
                protocols.insert(name.to_owned(), toml::Value::String(url.clone()));
            } else {
                protocols.remove(name);
            }
        }
    }
    write_toml_atomic(&provider_path, &provider_value)?;

    {
        let providers = ensure_table(&mut config_value, "providers")?;
        let entry = providers
            .get_mut(&params.id)
            .ok_or_else(|| ConfigError::UnknownProvider(params.id.clone()))?;
        let table = entry.as_table_mut().ok_or_else(|| {
            ConfigError::SerializeToml(format!("`providers.{}` must be a table", params.id))
        })?;
        table.insert("enabled".to_owned(), toml::Value::Boolean(params.enabled));
        table.insert("path".to_owned(), toml::Value::String(stored_path.clone()));
    }
    let first_enabled_model = params
        .models
        .iter()
        .find(|model| model.enabled)
        .map(|model| model.id.clone())
        .unwrap_or_default();
    {
        let agent = ensure_table(&mut config_value, "agent")?;
        let active_provider = agent
            .get("provider")
            .and_then(toml::Value::as_str)
            .unwrap_or_default()
            .to_owned();
        if default_missing {
            agent.insert(
                "provider".to_owned(),
                toml::Value::String(params.id.clone()),
            );
        }
        if default_missing || active_provider == params.id {
            let active_model = agent
                .get("model")
                .and_then(toml::Value::as_str)
                .unwrap_or_default();
            let model_is_enabled = params
                .models
                .iter()
                .any(|model| model.id == active_model && model.enabled);
            if !model_is_enabled {
                agent.insert("model".to_owned(), toml::Value::String(first_enabled_model));
            }
        }
    }
    write_toml_atomic(config_path, &config_value)?;

    Ok(ProviderUpsertResult {
        provider: params.id.clone(),
        path: provider_path.display().to_string(),
        models: file.models.len(),
        enabled: params.enabled,
    })
}

pub fn provider_set_api_key(
    config_path: &Path,
    home: &Path,
    params: &ProviderSetApiKeyParams,
) -> Result<ProviderKeyResult, ConfigError> {
    if !is_valid_provider_id(&params.id) {
        return Err(ConfigError::InvalidProviderId(params.id.clone()));
    }
    let config_value = read_toml_value(config_path)?;
    let exists = config_value
        .get("providers")
        .and_then(toml::Value::as_table)
        .is_some_and(|providers| providers.contains_key(&params.id));
    if !exists {
        return Err(ConfigError::UnknownProvider(params.id.clone()));
    }
    let key = params.key.trim();
    if key.is_empty() {
        return Err(ConfigError::EmptyApiKey);
    }
    let credentials = home.join("credentials");
    std::fs::create_dir_all(&credentials)?;
    write_bytes_atomic(
        &credentials.join(format!("{}.key", params.id)),
        key.as_bytes(),
        true,
    )?;
    Ok(ProviderKeyResult {
        provider: params.id.clone(),
        key_configured: true,
    })
}

pub fn provider_delete(
    config_path: &Path,
    home: &Path,
    id: &str,
) -> Result<ProviderDeleteResult, ConfigError> {
    if !is_valid_provider_id(id) {
        return Err(ConfigError::InvalidProviderId(id.to_owned()));
    }

    let config_dir = Config::config_dir(config_path);
    let mut config_value = read_toml_value(config_path)?;
    let provider_path = {
        let providers = ensure_table(&mut config_value, "providers")?;
        let stored_path = providers
            .get(id)
            .and_then(toml::Value::as_table)
            .and_then(|entry| entry.get("path"))
            .and_then(toml::Value::as_str)
            .filter(|path| !path.trim().is_empty())
            .map(str::to_owned)
            .unwrap_or_else(|| format!("providers/{id}.toml"));
        if providers.remove(id).is_none() {
            return Err(ConfigError::UnknownProvider(id.to_owned()));
        }
        resolve_path(&config_dir, &stored_path)
    };

    {
        let agent = ensure_table(&mut config_value, "agent")?;
        if agent.get("provider").and_then(toml::Value::as_str) == Some(id) {
            agent.insert("provider".to_owned(), toml::Value::String(String::new()));
            agent.insert("model".to_owned(), toml::Value::String(String::new()));
        }
    }
    write_toml_atomic(config_path, &config_value)?;

    // Drop the provider file and secret so re-adding starts clean.
    if provider_path.starts_with(&config_dir) {
        let _ = std::fs::remove_file(provider_path);
    }
    let _ = std::fs::remove_file(home.join("credentials").join(format!("{id}.key")));
    Ok(ProviderDeleteResult {
        provider: id.to_owned(),
        deleted: true,
    })
}

pub fn provider_set_enabled(
    config_path: &Path,
    params: &ProviderSetEnabledParams,
) -> Result<ProviderEnabledResult, ConfigError> {
    if !is_valid_provider_id(&params.id) {
        return Err(ConfigError::InvalidProviderId(params.id.clone()));
    }
    let mut config_value = read_toml_value(config_path)?;
    {
        let entry = provider_entry_mut(&mut config_value, &params.id)?;
        entry.insert("enabled".to_owned(), toml::Value::Boolean(params.enabled));
    }
    write_toml_atomic(config_path, &config_value)?;
    Ok(ProviderEnabledResult {
        provider: params.id.clone(),
        enabled: params.enabled,
    })
}

pub fn config_set_default_model(
    config_path: &Path,
    home: &Path,
    params: &SetDefaultModelParams,
) -> Result<DefaultModelResult, ConfigError> {
    if !is_valid_provider_id(&params.provider) {
        return Err(ConfigError::InvalidProviderId(params.provider.clone()));
    }
    let config_value = read_toml_value(config_path)?;
    let config: Config = config_value
        .clone()
        .try_into()
        .map_err(|error: toml::de::Error| ConfigError::Parse {
            path: config_path.display().to_string(),
            message: redact_api_key(&error.to_string()),
        })?;
    let config_dir = Config::config_dir(config_path);
    let catalog = ProviderCatalog::load(&config, &config_dir, home)?;
    if catalog.disabled.contains(&params.provider) {
        return Err(ConfigError::ProviderDisabled(params.provider.clone()));
    }
    let provider = catalog
        .providers
        .iter()
        .find(|provider| provider.id == params.provider)
        .ok_or_else(|| ConfigError::UnknownProvider(params.provider.clone()))?;
    let Some(model) = provider
        .models
        .iter()
        .find(|model| model.model == params.model)
    else {
        return Err(ConfigError::UnknownModel {
            provider: params.provider.clone(),
            model: params.model.clone(),
        });
    };
    if !model.enabled {
        return Err(ConfigError::ModelDisabled {
            provider: params.provider.clone(),
            model: params.model.clone(),
        });
    }
    let mut config_value = config_value;
    {
        let agent = ensure_table(&mut config_value, "agent")?;
        agent.insert(
            "provider".to_owned(),
            toml::Value::String(params.provider.clone()),
        );
        agent.insert(
            "model".to_owned(),
            toml::Value::String(params.model.clone()),
        );
    }
    write_toml_atomic(config_path, &config_value)?;
    Ok(DefaultModelResult {
        provider: params.provider.clone(),
        model: params.model.clone(),
    })
}

pub fn approval_flags(approval_mode: ApprovalMode) -> Option<(bool, bool, bool)> {
    match approval_mode {
        ApprovalMode::AcceptEdits => Some((true, false, false)),
        ApprovalMode::DontAsk => Some((true, true, true)),
        ApprovalMode::Ask | ApprovalMode::Plan | ApprovalMode::ReadOnly => {
            Some((false, false, false))
        }
        ApprovalMode::Auto => None,
    }
}

pub fn permissions_set(config_path: &Path, approval_mode: ApprovalMode) -> Result<(), ConfigError> {
    let mut value = read_toml_value(config_path)?;
    {
        let agent = ensure_table(&mut value, "agent")?;
        agent.insert(
            "approval_mode".to_owned(),
            toml::Value::String(approval_mode.as_str().to_owned()),
        );
        if let Some((edits, shell, plugins)) = approval_flags(approval_mode) {
            let policy = ensure_table(&mut value, "policy")?;
            for (key, enabled) in [
                ("auto_approve_edits", edits),
                ("auto_approve_shell", shell),
                ("auto_approve_plugins", plugins),
            ] {
                policy.insert(key.to_owned(), toml::Value::Boolean(enabled));
            }
        }
    }
    write_toml_atomic(config_path, &value)
}

pub const MAX_CUSTOM_INSTRUCTIONS: usize = 20_000;

pub fn agent_config_set(
    config_path: &Path,
    params: &AgentConfigSetParams,
) -> Result<(), ConfigError> {
    let mut value = read_toml_value(config_path)?;
    let agent = ensure_table(&mut value, "agent")?;
    if let Some(reasoning_effort) = params.reasoning_effort {
        agent.insert(
            "reasoning_effort".to_owned(),
            toml::Value::String(reasoning_effort.as_str().to_owned()),
        );
    }
    for (key, enabled) in [
        ("web_search", params.web_search),
        ("network_access", params.network_access),
        ("memory_enabled", params.memory_enabled),
        ("plugins_enabled", params.plugins_enabled),
    ] {
        if let Some(enabled) = enabled {
            agent.insert(key.to_owned(), toml::Value::Boolean(enabled));
        }
    }
    if let Some(verbosity) = params.output_verbosity {
        agent.insert(
            "output_verbosity".to_owned(),
            toml::Value::String(verbosity.as_str().to_owned()),
        );
    }
    if let Some(summary) = params.reasoning_summary {
        agent.insert(
            "reasoning_summary".to_owned(),
            toml::Value::String(summary.as_str().to_owned()),
        );
    }
    if let Some(instructions) = &params.custom_instructions {
        let instructions = sanitize_custom_instructions(instructions);
        if instructions.is_empty() {
            agent.remove("custom_instructions");
        } else {
            agent.insert(
                "custom_instructions".to_owned(),
                toml::Value::String(instructions),
            );
        }
    }
    for (key, value) in [
        (
            "personal_style",
            params.personal_style.map(|value| value.as_str()),
        ),
        ("warmth", params.warmth.map(|value| value.as_str())),
        ("enthusiasm", params.enthusiasm.map(|value| value.as_str())),
        (
            "emoji_usage",
            params.emoji_usage.map(|value| value.as_str()),
        ),
        (
            "visual_answers",
            params.visual_answers.map(|value| value.as_str()),
        ),
        (
            "heading_style",
            params.heading_style.map(|value| value.as_str()),
        ),
    ] {
        if let Some(value) = value {
            agent.insert(key.to_owned(), toml::Value::String(value.to_owned()));
        }
    }
    if let Some(enabled) = params.quick_answers {
        agent.insert("quick_answers".to_owned(), toml::Value::Boolean(enabled));
    }
    write_toml_atomic(config_path, &value)
}

pub fn sanitize_custom_instructions(instructions: &str) -> String {
    instructions
        .trim()
        .chars()
        .take(MAX_CUSTOM_INSTRUCTIONS)
        .collect()
}

#[cfg(test)]
mod tests;
