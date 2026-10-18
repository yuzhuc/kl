use kl_policy::PolicyConfig;
use kl_protocol::{
    ApprovalMode, DefaultModelResult, ProviderEnabledResult, ProviderKeyResult,
    ProviderSetApiKeyParams, ProviderSetEnabledParams, ProviderUpsertParams, ProviderUpsertResult,
    ReasoningEffort, SetDefaultModelParams,
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
    pub protocol: Protocol,
    pub context_window: u32,
    pub compact_threshold: f32,
    pub max_output_tokens: u32,
    /// Optional per-model override; when present it is the complete request URL.
    #[serde(default)]
    pub url: Option<String>,
}

#[derive(Debug, Clone)]
pub struct LoadedProvider {
    pub id: String,
    pub path: PathBuf,
    pub api_key: Option<String>,
    pub api_key_env: Option<String>,
    pub models: Vec<ModelMetadata>,
}

#[derive(Debug, Clone, Default)]
pub struct ProviderCatalog {
    pub providers: Vec<LoadedProvider>,
    pub disabled: HashSet<String>,
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
    #[error("provider `{0}` is disabled")]
    ProviderDisabled(String),
    #[error("provider `{provider}` has no model `{model}`")]
    UnknownModel { provider: String, model: String },
    #[error("invalid provider id `{0}`")]
    InvalidProviderId(String),
    #[error("API key must not be empty")]
    EmptyApiKey,
    #[error("cannot serialize TOML: {0}")]
    SerializeToml(String),
    #[error("provider `{0}` has no models")]
    NoModels(String),
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
        if !self.agent.provider.is_empty()
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
                models.push(ModelMetadata::new(
                    id.clone(),
                    model.id.clone(),
                    model.protocol,
                    url,
                    model.context_window,
                    model.compact_threshold,
                    model.max_output_tokens,
                ));
            }
            catalog.providers.push(LoadedProvider {
                id: id.clone(),
                path,
                api_key,
                api_key_env: file.api_key_env,
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
    if file.models.is_empty() {
        return Err(ConfigError::NoModels(id.to_owned()));
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
                protocol,
                context_window: model.context_window,
                compact_threshold: model.compact_threshold,
                max_output_tokens: model.max_output_tokens,
                url: clean(model.url.clone()),
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
                if let Some(url) = &model.url {
                    model_table.insert("url".to_owned(), toml::Value::String(url.clone()));
                } else {
                    model_table.remove("url");
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
    if !provider
        .models
        .iter()
        .any(|model| model.model == params.model)
    {
        return Err(ConfigError::UnknownModel {
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

pub fn permissions_set(config_path: &Path, approval_mode: ApprovalMode) -> Result<(), ConfigError> {
    let mut value = read_toml_value(config_path)?;
    {
        let agent = ensure_table(&mut value, "agent")?;
        agent.insert(
            "approval_mode".to_owned(),
            toml::Value::String(approval_mode.as_str().to_owned()),
        );
        let policy = ensure_table(&mut value, "policy")?;
        let auto_approve = approval_mode == ApprovalMode::Auto;
        for key in [
            "auto_approve_edits",
            "auto_approve_shell",
            "auto_approve_plugins",
        ] {
            policy.insert(key.to_owned(), toml::Value::Boolean(auto_approve));
        }
    }
    write_toml_atomic(config_path, &value)
}

pub fn agent_config_set(
    config_path: &Path,
    reasoning_effort: ReasoningEffort,
) -> Result<(), ConfigError> {
    let mut value = read_toml_value(config_path)?;
    let agent = ensure_table(&mut value, "agent")?;
    agent.insert(
        "reasoning_effort".to_owned(),
        toml::Value::String(reasoning_effort.as_str().to_owned()),
    );
    write_toml_atomic(config_path, &value)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn three_protocol_config() -> (&'static str, &'static str) {
        (
            r#"
[agent]
provider = "opencode"
model = "gpt-5-responses"

[providers.opencode]
enabled = true
path = "providers/opencode.toml"
"#,
            r#"
api_key_env = "OPENCODE_API_KEY"

[protocols]
openai-chat = "https://gateway.example/chat/complete"
anthropic = "https://gateway.example/messages"
openai-responses = "https://gateway.example/responses"

[[models]]
id = "gpt-5-chat"
protocol = "openai-chat"
context_window = 128000
compact_threshold = 0.8
max_output_tokens = 4096

[[models]]
id = "claude-native"
protocol = "anthropic"
context_window = 200000
compact_threshold = 0.75
max_output_tokens = 8192

[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 256000
compact_threshold = 0.9
max_output_tokens = 16384
url = "https://gateway.example/custom/responses"
"#,
        )
    }

    #[test]
    fn incomplete_urls_are_rejected() {
        assert!(!is_complete_url("http://"));
        assert!(!is_complete_url("http:///v1/chat"));
        assert!(!is_complete_url("gateway.example/v1/chat"));
        assert!(is_complete_url("https://gateway.example/v1/chat"));
    }

    #[test]
    fn invalid_context_and_output_limits_are_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
        let provider = r#"
api_key = "inline-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 0
compact_threshold = 0.5
max_output_tokens = 100
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        let provider_path = temp.path().join("test.toml");
        std::fs::write(&provider_path, provider).unwrap();
        let config = Config::load(&global_path).unwrap();
        let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
        assert!(matches!(error, ConfigError::InvalidContextWindow { .. }));

        let provider = provider.replace("context_window = 0", "context_window = 1000");
        let provider = provider.replace("max_output_tokens = 100", "max_output_tokens = 0");
        std::fs::write(&provider_path, provider).unwrap();
        let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
        assert!(matches!(error, ConfigError::InvalidMaxOutput { .. }));
    }

    #[test]
    fn provider_parse_errors_redact_inline_keys() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
        let provider = r#"
api_key = "super-secret-key"
[[models]
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        std::fs::write(temp.path().join("test.toml"), provider).unwrap();
        let config = Config::load(&global_path).unwrap();
        let error = ProviderCatalog::load(&config, temp.path(), temp.path())
            .unwrap_err()
            .to_string();
        assert!(!error.contains("super-secret-key"));
        assert!(!redact_api_key("api_key = \"secret\"").contains("secret"));
    }

    #[test]
    fn loads_multiple_providers_independently() {
        let temp = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(temp.path().join("providers")).unwrap();
        std::fs::write(
            temp.path().join("providers/a.toml"),
            r#"
api_key = "a-key"
[protocols]
openai-chat = "https://gateway.example/a/chat"
[[models]]
id = "a-model"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#,
        )
        .unwrap();
        std::fs::write(
            temp.path().join("providers/b.toml"),
            r#"
api_key = "b-key"
[protocols]
anthropic = "https://gateway.example/b/messages"
[[models]]
id = "b-model"
protocol = "anthropic"
context_window = 2000
compact_threshold = 0.6
max_output_tokens = 200
"#,
        )
        .unwrap();
        let global_path = temp.path().join("config.toml");
        std::fs::write(
            &global_path,
            r#"
[agent]
provider = "a"
model = "a-model"
[providers.a]
enabled = true
path = "providers/a.toml"
[providers.b]
enabled = true
path = "providers/b.toml"
"#,
        )
        .unwrap();
        let config = Config::load(&global_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert_eq!(catalog.providers.len(), 2);
        assert_eq!(catalog.providers[0].models[0].model, "a-model");
        assert_eq!(catalog.providers[1].models[0].model, "b-model");
    }

    #[test]
    fn parses_three_protocol_provider_catalog() {
        let temp = tempfile::tempdir().unwrap();
        let (global, provider) = three_protocol_config();
        std::fs::create_dir_all(temp.path().join("providers")).unwrap();
        std::fs::write(temp.path().join("providers/opencode.toml"), provider).unwrap();
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        let config = Config::load(&global_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert_eq!(catalog.providers.len(), 1);
        assert_eq!(catalog.providers[0].models.len(), 3);
        assert_eq!(
            catalog.providers[0].models[2].url,
            "https://gateway.example/custom/responses"
        );
    }

    #[test]
    fn disabled_provider_is_not_loaded() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[agent]
provider = "opencode"
model = "gpt-5-chat"

[providers.opencode]
enabled = false
path = "providers/missing.toml"
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        let config = Config::load(&global_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert!(catalog.providers.is_empty());
        assert!(catalog.disabled.contains("opencode"));
    }

    #[test]
    fn missing_protocol_url_is_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
        let provider = r#"
api_key = "inline-key"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        std::fs::write(temp.path().join("test.toml"), provider).unwrap();
        let config = Config::load(&global_path).unwrap();
        let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
        assert!(matches!(error, ConfigError::MissingProtocolUrl { .. }));
    }

    #[test]
    fn invalid_threshold_and_duplicate_models_are_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
        let provider = r#"
api_key = "inline-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "same"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 1.5
max_output_tokens = 100
[[models]]
id = "same"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        std::fs::write(temp.path().join("test.toml"), provider).unwrap();
        let config = Config::load(&global_path).unwrap();
        let error = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap_err();
        assert!(matches!(error, ConfigError::DuplicateModel { .. }));
    }

    #[test]
    fn redacted_provider_catalog_does_not_leak_inline_key() {
        let temp = tempfile::tempdir().unwrap();
        let global = r#"
[providers.test]
enabled = true
path = "test.toml"
"#;
        let provider = r#"
api_key = "super-secret-key"
[protocols]
openai-chat = "https://example.test/chat"
[[models]]
id = "chat"
protocol = "openai-chat"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
"#;
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        std::fs::write(temp.path().join("test.toml"), provider).unwrap();
        let config = Config::load(&global_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        let redacted = catalog.redacted().to_string();
        assert!(!redacted.contains("super-secret-key"));
        assert!(redacted.contains("configured"));
    }

    #[test]
    fn provider_upsert_preserves_unknown_fields_and_writes_atomically() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("config.toml");
        let provider_dir = temp.path().join("providers");
        std::fs::create_dir_all(&provider_dir).unwrap();
        std::fs::write(
            &config_path,
            r#"
[custom]
keep = "yes"

[providers.opencode]
enabled = false
path = "providers/opencode.toml"
note = "keep"
"#,
        )
        .unwrap();
        std::fs::write(
            provider_dir.join("opencode.toml"),
            r#"
note = "keep"
api_key_env = "OPENCODE_API_KEY"

[protocols]
openai-chat = "https://old.example/chat"

[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 1000
compact_threshold = 0.5
max_output_tokens = 100
note = "keep-model"
"#,
        )
        .unwrap();

        let result = provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
        assert_eq!(result.models, 1);
        assert!(result.enabled);

        let config_text = std::fs::read_to_string(&config_path).unwrap();
        assert!(config_text.contains("keep = \"yes\""));
        assert!(config_text.contains("note = \"keep\""));
        assert!(config_text.contains("enabled = true"));

        let provider_text = std::fs::read_to_string(provider_dir.join("opencode.toml")).unwrap();
        assert!(provider_text.contains("note = \"keep\""));
        assert!(provider_text.contains("note = \"keep-model\""));
        assert!(provider_text.contains("openai-responses = \"https://gateway.example/responses\""));
        assert!(!provider_text.contains("api_key ="));

        provider_upsert(&config_path, &upsert_params("opencode", false)).unwrap();
        let config_text = std::fs::read_to_string(&config_path).unwrap();
        assert!(config_text.contains("enabled = false"));
        assert!(config_text.contains("keep = \"yes\""));
        let provider_text = std::fs::read_to_string(provider_dir.join("opencode.toml")).unwrap();
        assert!(provider_text.contains("note = \"keep-model\""));
        for directory in [temp.path(), provider_dir.as_path()] {
            for entry in std::fs::read_dir(directory).unwrap() {
                let name = entry.unwrap().file_name().to_string_lossy().into_owned();
                assert!(!name.contains(".tmp"), "temporary file left behind: {name}");
            }
        }
    }

    #[test]
    fn api_key_is_written_outside_provider_file_and_redacted() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("config.toml");
        provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
        provider_set_api_key(
            &config_path,
            temp.path(),
            &ProviderSetApiKeyParams {
                id: "opencode".to_owned(),
                key: "super-secret-key".to_owned(),
            },
        )
        .unwrap();

        let key_path = temp.path().join("credentials/opencode.key");
        assert_eq!(
            std::fs::read_to_string(&key_path).unwrap(),
            "super-secret-key"
        );
        let provider_text =
            std::fs::read_to_string(temp.path().join("providers/opencode.toml")).unwrap();
        assert!(!provider_text.contains("super-secret-key"));

        let config = Config::load(&config_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert_eq!(
            catalog.providers[0].api_key.as_deref(),
            Some("super-secret-key")
        );
        let redacted = catalog.redacted().to_string();
        assert!(!redacted.contains("super-secret-key"));
        let redacted_value: Value = catalog.redacted();
        assert_eq!(redacted_value["providers"][0]["key_configured"], true);
    }

    #[test]
    fn default_model_and_enabled_validation_use_saved_config() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("config.toml");
        provider_upsert(&config_path, &upsert_params("opencode", true)).unwrap();
        provider_set_api_key(
            &config_path,
            temp.path(),
            &ProviderSetApiKeyParams {
                id: "opencode".to_owned(),
                key: "test-key".to_owned(),
            },
        )
        .unwrap();

        let result = config_set_default_model(
            &config_path,
            temp.path(),
            &SetDefaultModelParams {
                provider: "opencode".to_owned(),
                model: "gpt-5-responses".to_owned(),
            },
        )
        .unwrap();
        assert_eq!(result.provider, "opencode");
        assert_eq!(result.model, "gpt-5-responses");
        let config = Config::load(&config_path).unwrap();
        assert_eq!(config.agent.provider, "opencode");
        assert_eq!(config.agent.model, "gpt-5-responses");

        provider_set_enabled(
            &config_path,
            &ProviderSetEnabledParams {
                id: "opencode".to_owned(),
                enabled: false,
            },
        )
        .unwrap();
        let error = config_set_default_model(
            &config_path,
            temp.path(),
            &SetDefaultModelParams {
                provider: "opencode".to_owned(),
                model: "gpt-5-responses".to_owned(),
            },
        )
        .unwrap_err();
        assert!(matches!(error, ConfigError::ProviderDisabled(_)));
        let config = Config::load(&config_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert!(catalog.providers.is_empty());
        assert!(catalog.disabled.contains("opencode"));
    }

    #[test]
    fn invalid_provider_ids_are_rejected() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("config.toml");
        let error = provider_upsert(&config_path, &upsert_params("../escape", true)).unwrap_err();
        assert!(matches!(error, ConfigError::InvalidProviderId(_)));
    }

    #[test]
    fn api_key_env_overrides_credentials_file() {
        let temp = tempfile::tempdir().unwrap();
        let config_path = temp.path().join("config.toml");
        let env_name = format!("KL_TEST_PROVIDER_KEY_{}", std::process::id());
        unsafe {
            std::env::set_var(&env_name, "env-secret");
        }
        let mut params = upsert_params("local", true);
        params.api_key_env = Some(env_name.clone());
        provider_upsert(&config_path, &params).unwrap();
        provider_set_api_key(
            &config_path,
            temp.path(),
            &ProviderSetApiKeyParams {
                id: "local".to_owned(),
                key: "file-secret".to_owned(),
            },
        )
        .unwrap();
        let config = Config::load(&config_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path(), temp.path()).unwrap();
        assert_eq!(catalog.providers[0].api_key.as_deref(), Some("env-secret"));
        unsafe {
            std::env::remove_var(&env_name);
        }
    }
    fn upsert_params(id: &str, enabled: bool) -> ProviderUpsertParams {
        ProviderUpsertParams {
            id: id.to_owned(),
            protocols: kl_protocol::ProviderProtocolUrls {
                openai_chat: Some("https://gateway.example/chat".to_owned()),
                anthropic: Some("https://gateway.example/messages".to_owned()),
                openai_responses: Some("https://gateway.example/responses".to_owned()),
            },
            models: vec![kl_protocol::ProviderModelInput {
                id: "gpt-5-responses".to_owned(),
                protocol: "openai-responses".to_owned(),
                context_window: 1000,
                compact_threshold: 0.5,
                max_output_tokens: 100,
                url: None,
            }],
            enabled,
            api_key_env: None,
        }
    }
}
