use crate::manifest::PluginManifest;
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ring::signature::{self, UnparsedPublicKey};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::fmt::Write as _;
use std::path::Path;

pub const PUBLIC_KEY_ENV: &str = "KL_PLUGIN_PUBLIC_KEY";
pub const SECURITY_CONFIG_FILE: &str = "security.json";

/// Signature policy for a plugin host.
///
/// The public key is a standard-base64 encoded raw 32-byte Ed25519 public
/// key. When it is absent, KL_PLUGIN_PUBLIC_KEY is used as a fallback.
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct PluginSecurityConfig {
    pub require_signature: bool,
    pub public_key: Option<String>,
}

impl PluginSecurityConfig {
    pub(crate) fn load(root: &Path) -> Result<Self, IntegrityError> {
        let path = root.join(SECURITY_CONFIG_FILE);
        match std::fs::read_to_string(&path) {
            Ok(text) => serde_json::from_str(&text)
                .map_err(|error| IntegrityError::Config(format!("{}: {error}", path.display()))),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(Self::default()),
            Err(error) => Err(IntegrityError::Io(error)),
        }
    }
}

#[derive(Debug, thiserror::Error)]
pub enum IntegrityError {
    #[error("plugin security config is invalid: {0}")]
    Config(String),
    #[error("plugin integrity io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("plugin integrity metadata is missing")]
    MissingIntegrity,
    #[error("plugin integrity digest is not a SHA-256 hex string")]
    InvalidDigest,
    #[error("plugin entrypoint hash does not match integrity.sha256")]
    HashMismatch,
    #[error("plugin signature is required but missing")]
    MissingSignature,
    #[error("plugin signature requires integrity metadata")]
    SignatureWithoutIntegrity,
    #[error("plugin public key is not configured")]
    MissingPublicKey,
    #[error("plugin public key is not a base64-encoded 32-byte Ed25519 key")]
    InvalidPublicKey,
    #[error("unsupported plugin signature algorithm {0}")]
    UnsupportedAlgorithm(String),
    #[error("plugin signature is not valid base64")]
    InvalidSignature,
    #[error("plugin signature verification failed")]
    SignatureMismatch,
}

pub(crate) struct PluginVerifier {
    require_signature: bool,
    public_key: Option<[u8; 32]>,
}

impl PluginVerifier {
    pub(crate) fn new(config: &PluginSecurityConfig) -> Result<Self, IntegrityError> {
        let raw = config
            .public_key
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_owned)
            .or_else(|| {
                std::env::var(PUBLIC_KEY_ENV)
                    .ok()
                    .map(|value| value.trim().to_owned())
                    .filter(|value| !value.is_empty())
            });
        let public_key = raw.map(|value| decode_public_key(&value)).transpose()?;
        if config.require_signature && public_key.is_none() {
            return Err(IntegrityError::MissingPublicKey);
        }
        Ok(Self {
            require_signature: config.require_signature,
            public_key,
        })
    }

    /// The single integrity gate used before any sidecar or WASM instance is
    /// started or replaced.
    pub(crate) fn verify(
        &self,
        manifest: &PluginManifest,
        manifest_path: &Path,
    ) -> Result<(), IntegrityError> {
        let entrypoint = manifest.entrypoint_path(manifest_path);
        let actual = sha256_hex(&std::fs::read(entrypoint)?);
        let Some(integrity) = &manifest.integrity else {
            if manifest.signature.is_some() {
                return Err(IntegrityError::SignatureWithoutIntegrity);
            }
            return if self.require_signature {
                Err(IntegrityError::MissingIntegrity)
            } else {
                Ok(())
            };
        };
        let expected = integrity.sha256.trim().to_ascii_lowercase();
        if expected.len() != 64 || !expected.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err(IntegrityError::InvalidDigest);
        }
        if actual != expected {
            return Err(IntegrityError::HashMismatch);
        }

        let Some(signature) = &manifest.signature else {
            return if self.require_signature {
                Err(IntegrityError::MissingSignature)
            } else {
                Ok(())
            };
        };
        if signature.algorithm != "ed25519" {
            return Err(IntegrityError::UnsupportedAlgorithm(
                signature.algorithm.clone(),
            ));
        }
        let public_key = self
            .public_key
            .as_ref()
            .ok_or(IntegrityError::MissingPublicKey)?;
        let signature_bytes = BASE64
            .decode(signature.value.trim())
            .map_err(|_| IntegrityError::InvalidSignature)?;
        UnparsedPublicKey::new(&signature::ED25519, public_key.as_slice())
            .verify(&manifest.signing_payload(), &signature_bytes)
            .map_err(|_| IntegrityError::SignatureMismatch)
    }
}

fn decode_public_key(value: &str) -> Result<[u8; 32], IntegrityError> {
    BASE64
        .decode(value)
        .map_err(|_| IntegrityError::InvalidPublicKey)?
        .try_into()
        .map_err(|_| IntegrityError::InvalidPublicKey)
}

fn sha256_hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let mut output = String::with_capacity(64);
    for byte in digest {
        write!(output, "{byte:02x}").expect("writing to a String cannot fail");
    }
    output
}
