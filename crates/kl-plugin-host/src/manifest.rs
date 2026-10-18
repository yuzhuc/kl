use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const PLUGIN_API_VERSION: u32 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PluginKind {
    Provider,
    Tool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct PluginPermissions {
    pub network: Vec<String>,
    pub env: Vec<String>,
    /// `workspace` or workspace-relative prefixes.
    pub fs_read: Vec<String>,
    /// `workspace` or workspace-relative prefixes.
    pub fs_write: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PluginIntegrity {
    /// Lowercase or uppercase hex SHA-256 of the entrypoint file.
    pub sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PluginSignature {
    pub algorithm: String,
    pub value: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PluginManifest {
    pub id: String,
    pub version: String,
    pub api: u32,
    pub kind: PluginKind,
    pub entrypoint: String,
    #[serde(default = "default_runtime")]
    pub runtime: String,
    #[serde(default)]
    pub capabilities: Vec<String>,
    #[serde(default)]
    pub permissions: PluginPermissions,
    #[serde(default)]
    pub tool_name: Option<String>,
    #[serde(default)]
    pub tool_parameters: Option<serde_json::Value>,
    #[serde(default)]
    pub models: Vec<String>,
    #[serde(default)]
    pub config_path: Option<String>,
    #[serde(default = "default_health_timeout")]
    pub health_timeout_ms: u64,
    #[serde(default = "default_drain_timeout")]
    pub drain_timeout_ms: u64,
    #[serde(default = "default_call_timeout")]
    pub call_timeout_ms: u64,
    #[serde(default = "default_memory_limit")]
    pub memory_limit_mb: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub integrity: Option<PluginIntegrity>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub signature: Option<PluginSignature>,
}

fn default_runtime() -> String {
    "sidecar".to_owned()
}

fn default_health_timeout() -> u64 {
    3000
}

fn default_drain_timeout() -> u64 {
    5000
}

fn default_call_timeout() -> u64 {
    30000
}

fn default_memory_limit() -> u64 {
    64
}

#[derive(Debug, thiserror::Error)]
pub enum ManifestError {
    #[error("manifest io: {0}")]
    Io(#[from] std::io::Error),
    #[error("manifest json: {0}")]
    Json(#[from] serde_json::Error),
    #[error("manifest toml: {0}")]
    Toml(#[from] toml::de::Error),
    #[error("unsupported plugin API {0}; expected {PLUGIN_API_VERSION}")]
    Api(u32),
    #[error("unsupported plugin runtime `{0}`; expected `sidecar` or `wasm`")]
    Runtime(String),
    #[error("the wasm runtime only supports tool plugins")]
    WasmProvider,
    #[error("plugin id must contain only letters, digits, '-' or '_'")]
    InvalidId,
    #[error("plugin `{0}` is missing a tool name")]
    MissingToolName(String),
    #[error("plugin provider `{0}` has no models")]
    MissingModels(String),
    #[error("plugin entrypoint does not exist: {0}")]
    MissingEntrypoint(PathBuf),
}

impl PluginManifest {
    pub fn load(path: &Path) -> Result<Self, ManifestError> {
        let text = std::fs::read_to_string(path)?;
        let manifest: Self = match path.extension().and_then(|ext| ext.to_str()) {
            Some("toml") => toml::from_str(&text)?,
            _ => serde_json::from_str(&text)?,
        };
        manifest.validate(path)?;
        Ok(manifest)
    }

    pub fn validate(&self, manifest_path: &Path) -> Result<(), ManifestError> {
        if self.api != PLUGIN_API_VERSION {
            return Err(ManifestError::Api(self.api));
        }
        match self.runtime.as_str() {
            "sidecar" => {}
            "wasm" if self.kind == PluginKind::Tool => {}
            "wasm" => return Err(ManifestError::WasmProvider),
            _ => return Err(ManifestError::Runtime(self.runtime.clone())),
        }
        if self.id.is_empty()
            || !self.id.chars().all(|character| {
                character.is_ascii_alphanumeric() || character == '-' || character == '_'
            })
        {
            return Err(ManifestError::InvalidId);
        }
        if self.kind == PluginKind::Tool && self.tool_name.as_deref().unwrap_or("").is_empty() {
            return Err(ManifestError::MissingToolName(self.id.clone()));
        }
        if self.kind == PluginKind::Provider && self.models.is_empty() {
            return Err(ManifestError::MissingModels(self.id.clone()));
        }
        let entrypoint = self.entrypoint_path(manifest_path);
        if !entrypoint.exists() {
            return Err(ManifestError::MissingEntrypoint(entrypoint));
        }
        Ok(())
    }

    pub fn manifest_dir(manifest_path: &Path) -> PathBuf {
        manifest_path
            .parent()
            .map(Path::to_path_buf)
            .unwrap_or_else(|| PathBuf::from("."))
    }

    pub fn entrypoint_path(&self, manifest_path: &Path) -> PathBuf {
        let entrypoint = Path::new(&self.entrypoint);
        if entrypoint.is_absolute() {
            entrypoint.to_path_buf()
        } else {
            Self::manifest_dir(manifest_path).join(entrypoint)
        }
    }

    pub fn config_path(&self, manifest_path: &Path) -> Option<PathBuf> {
        let raw = self.config_path.as_deref()?;
        let path = Path::new(raw);
        Some(if path.is_absolute() {
            path.to_path_buf()
        } else {
            Self::manifest_dir(manifest_path).join(path)
        })
    }

    pub fn tool_name(&self) -> String {
        self.tool_name.clone().unwrap_or_else(|| self.id.clone())
    }

    pub fn is_wasm(&self) -> bool {
        self.runtime == "wasm"
    }

    /// Canonical bytes covered by an Ed25519 signature: compact JSON for this
    /// manifest with the `signature` field removed.
    pub fn signing_payload(&self) -> Vec<u8> {
        let mut unsigned = self.clone();
        unsigned.signature = None;
        serde_json::to_vec(&unsigned).expect("plugin manifest is serializable")
    }

    pub fn models(&self) -> Vec<String> {
        if self.models.is_empty() {
            vec![format!("plugin-{}", self.id)]
        } else {
            self.models.clone()
        }
    }
}
