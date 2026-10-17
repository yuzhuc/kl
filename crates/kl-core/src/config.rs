use kl_policy::PolicyConfig;
use kl_provider::{ModelMetadata, Protocol};
use kl_sandbox::SandboxConfig;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::{BTreeMap, HashSet};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Config {
    pub agent: AgentSettings,
    pub policy: PolicyConfig,
    pub sandbox: SandboxConfig,
    pub audit: AuditConfig,
    pub providers: BTreeMap<String, ProviderEntry>,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
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

#[derive(Debug, Clone, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct AgentSettings {
    pub provider: String,
    pub model: String,
    /// Offline-only scripted provider source. Never used by native adapters.
    pub scripted_path: Option<String>,
    pub max_iterations: usize,
    pub max_tool_output_bytes: usize,
    pub command_timeout_secs: u64,
    pub approval_timeout_secs: u64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
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
#[serde(default, deny_unknown_fields)]
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
#[serde(deny_unknown_fields)]
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
    #[error("provider `{0}` must set exactly one of api_key_env or api_key")]
    InvalidCredentials(String),
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

impl Default for Config {
    fn default() -> Self {
        Self {
            agent: AgentSettings::default(),
            policy: PolicyConfig::default(),
            sandbox: SandboxConfig::default(),
            audit: AuditConfig::default(),
            providers: BTreeMap::new(),
        }
    }
}

impl Default for AgentSettings {
    fn default() -> Self {
        Self {
            provider: String::new(),
            model: String::new(),
            scripted_path: None,
            max_iterations: 12,
            max_tool_output_bytes: 32 * 1024,
            command_timeout_secs: 120,
            approval_timeout_secs: 180,
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
            message: error.to_string(),
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
                "max_iterations": self.agent.max_iterations,
                "max_tool_output_bytes": self.agent.max_tool_output_bytes,
                "command_timeout_secs": self.agent.command_timeout_secs,
                "approval_timeout_secs": self.agent.approval_timeout_secs,
            },
            "policy": self.policy,
            "sandbox": self.sandbox,
            "audit": self.audit,
            "providers": self.providers,
        })
    }
}

impl ProviderCatalog {
    pub fn load(config: &Config, config_dir: &Path) -> Result<Self, ConfigError> {
        let mut catalog = Self::default();
        for (id, entry) in &config.providers {
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
                    message: error.to_string(),
                })?;
            validate_provider_file(id, &path, &file)?;

            let api_key = file
                .api_key
                .clone()
                .filter(|key| !key.trim().is_empty())
                .or_else(|| {
                    file.api_key_env
                        .as_ref()
                        .and_then(|name| std::env::var(name).ok())
                        .filter(|key| !key.trim().is_empty())
                });
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
                "api_key_env": provider.api_key_env,
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
    if has_inline == has_env {
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

fn is_complete_url(url: &str) -> bool {
    let url = url.trim();
    (url.starts_with("http://") || url.starts_with("https://"))
        && !url.chars().any(char::is_whitespace)
}

fn resolve_path(config_dir: &Path, path: &str) -> PathBuf {
    let path = Path::new(path);
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        config_dir.join(path)
    }
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
    fn parses_three_protocol_provider_catalog() {
        let temp = tempfile::tempdir().unwrap();
        let (global, provider) = three_protocol_config();
        std::fs::create_dir_all(temp.path().join("providers")).unwrap();
        std::fs::write(temp.path().join("providers/opencode.toml"), provider).unwrap();
        let global_path = temp.path().join("config.toml");
        std::fs::write(&global_path, global).unwrap();
        let config = Config::load(&global_path).unwrap();
        let catalog = ProviderCatalog::load(&config, temp.path()).unwrap();
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
        let catalog = ProviderCatalog::load(&config, temp.path()).unwrap();
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
        let error = ProviderCatalog::load(&config, temp.path()).unwrap_err();
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
        let error = ProviderCatalog::load(&config, temp.path()).unwrap_err();
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
        let catalog = ProviderCatalog::load(&config, temp.path()).unwrap();
        let redacted = catalog.redacted().to_string();
        assert!(!redacted.contains("super-secret-key"));
        assert!(redacted.contains("configured"));
    }
}
