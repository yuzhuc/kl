use kl_policy::PolicyConfig;
use serde::Deserialize;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct Config {
    pub agent: AgentSettings,
    pub policy: PolicyConfig,
    pub providers: Vec<ProviderConfig>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct AgentSettings {
    pub provider: String,
    pub model: String,
    pub max_iterations: usize,
    pub max_tool_output_bytes: usize,
    pub command_timeout_secs: u64,
    pub approval_timeout_secs: u64,
    pub max_output_tokens: u32,
}

#[derive(Debug, Clone, Deserialize)]
pub struct ProviderConfig {
    pub id: String,
    pub kind: String,
    #[serde(default)]
    pub base_url: String,
    #[serde(default)]
    pub api_key_env: String,
    #[serde(default)]
    pub models: Vec<String>,
    #[serde(default)]
    pub script_path: Option<String>,
}

#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("cannot read config: {0}")]
    Io(#[from] std::io::Error),
    #[error("cannot parse config: {0}")]
    Parse(#[from] toml::de::Error),
    #[error("provider id `{0}` is duplicated")]
    DuplicateProvider(String),
    #[error("default provider `{0}` is not configured")]
    MissingDefaultProvider(String),
    #[error("provider `{id}` has unsupported kind `{kind}`")]
    UnsupportedProvider { id: String, kind: String },
    #[error("no providers are configured")]
    NoProviders,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            agent: AgentSettings::default(),
            policy: PolicyConfig::default(),
            providers: vec![
                ProviderConfig {
                    id: "openai".to_owned(),
                    kind: "openai-chat".to_owned(),
                    base_url: "https://api.openai.com/v1".to_owned(),
                    api_key_env: "OPENAI_API_KEY".to_owned(),
                    models: vec!["gpt-4o-mini".to_owned()],
                    script_path: None,
                },
                ProviderConfig {
                    id: "anthropic".to_owned(),
                    kind: "anthropic".to_owned(),
                    base_url: "https://api.anthropic.com".to_owned(),
                    api_key_env: "ANTHROPIC_API_KEY".to_owned(),
                    models: vec!["claude-sonnet-4-5".to_owned()],
                    script_path: None,
                },
                ProviderConfig {
                    id: "scripted".to_owned(),
                    kind: "scripted".to_owned(),
                    base_url: String::new(),
                    api_key_env: String::new(),
                    models: vec!["scripted".to_owned()],
                    script_path: None,
                },
            ],
        }
    }
}

impl Default for AgentSettings {
    fn default() -> Self {
        Self {
            provider: "openai".to_owned(),
            model: "gpt-4o-mini".to_owned(),
            max_iterations: 12,
            max_tool_output_bytes: 32 * 1024,
            command_timeout_secs: 120,
            approval_timeout_secs: 180,
            max_output_tokens: 4096,
        }
    }
}

impl Config {
    pub fn load(path: &Path) -> Result<Self, ConfigError> {
        let config = if path.exists() {
            let text = std::fs::read_to_string(path)?;
            toml::from_str(&text)?
        } else {
            Self::default()
        };
        config.validate()?;
        Ok(config)
    }

    pub fn validate(&self) -> Result<(), ConfigError> {
        if self.providers.is_empty() {
            return Err(ConfigError::NoProviders);
        }
        let mut seen = std::collections::HashSet::new();
        for provider in &self.providers {
            if !matches!(
                provider.kind.as_str(),
                "openai-chat" | "anthropic" | "scripted"
            ) {
                return Err(ConfigError::UnsupportedProvider {
                    id: provider.id.clone(),
                    kind: provider.kind.clone(),
                });
            }
            if !seen.insert(provider.id.as_str()) {
                return Err(ConfigError::DuplicateProvider(provider.id.clone()));
            }
        }
        if !seen.contains(self.agent.provider.as_str()) {
            return Err(ConfigError::MissingDefaultProvider(
                self.agent.provider.clone(),
            ));
        }
        Ok(())
    }

    pub fn config_dir(path: &Path) -> PathBuf {
        path.parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| PathBuf::from("."))
    }

    /// Config exposed to clients: never includes resolved secret values.
    pub fn redacted(&self) -> Value {
        json!({
            "agent": {
                "provider": self.agent.provider,
                "model": self.agent.model,
                "max_iterations": self.agent.max_iterations,
                "max_tool_output_bytes": self.agent.max_tool_output_bytes,
                "command_timeout_secs": self.agent.command_timeout_secs,
                "approval_timeout_secs": self.agent.approval_timeout_secs,
                "max_output_tokens": self.agent.max_output_tokens,
            },
            "policy": self.policy,
            "providers": self.providers.iter().map(|provider| json!({
                "id": provider.id,
                "kind": provider.kind,
                "base_url": provider.base_url,
                "api_key_env": provider.api_key_env,
                "models": provider.models,
                "script_path": provider.script_path,
            })).collect::<Vec<_>>(),
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_partial_config() {
        let config: Config = toml::from_str(
            r#"
            [agent]
            provider = "anthropic"
            model = "claude-test"

            [[providers]]
            id = "anthropic"
            kind = "anthropic"
            models = ["claude-test"]
            "#,
        )
        .unwrap();
        assert_eq!(config.agent.provider, "anthropic");
        assert_eq!(config.agent.max_iterations, 12);
        assert_eq!(config.providers.len(), 1);
    }

    #[test]
    fn rejects_duplicate_and_unknown_providers() {
        let mut config = Config::default();
        config.providers.push(config.providers[0].clone());
        assert!(matches!(
            config.validate(),
            Err(ConfigError::DuplicateProvider(_))
        ));

        let mut config = Config::default();
        config.providers[0].kind = "mystery".to_owned();
        assert!(matches!(
            config.validate(),
            Err(ConfigError::UnsupportedProvider { .. })
        ));
    }

    #[test]
    fn redacted_config_has_no_secret_values() {
        let config = Config::default();
        let value = config.redacted();
        assert_eq!(value["providers"][0]["api_key_env"], "OPENAI_API_KEY");
        assert!(!value.to_string().contains("sk-"));
    }
}
