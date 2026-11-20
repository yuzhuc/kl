//! Agent presets: the built-in roles plus user-defined `~/.kl/agents/*.toml`.
//!
//! A preset file is a flat TOML table:
//!
//! ```toml
//! id = "reviewer"
//! name = "Strict Reviewer"
//! description = "Reviews changes with a strict lens"
//! system = "You are a meticulous reviewer."
//! policy = "read-only"
//! max_iterations = 8
//! provider = "openai"
//! model = "gpt-5"
//! ```
//!
//! `id`, `name` and `policy` are required; the rest is optional. A custom
//! preset with a built-in id replaces that built-in preset, except for
//! `deep`, which keeps the built-in multi-stage pipeline.

use kl_policy::ToolAccess;
use serde::Deserialize;
use std::fs;
use std::path::Path;

use crate::config::AgentPreset;

/// Directory under the KL home that holds user-defined agent presets.
pub const AGENTS_DIR: &str = "agents";

/// Id of the built-in preset that runs the staged deep pipeline.
pub const DEEP_ID: &str = "deep";

/// One resolved agent preset, either built in or loaded from a TOML file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentProfile {
    pub id: String,
    pub name: String,
    pub description: String,
    /// Extra system prompt appended to the agent system prompt.
    pub system: Option<String>,
    /// Role text prepended to the user prompt.
    pub prompt_prefix: String,
    /// Tool access ceiling for this preset.
    pub policy: ToolAccess,
    pub max_iterations: Option<usize>,
    pub provider: Option<String>,
    pub model: Option<String>,
    pub built_in: bool,
    /// `builtin` for the built-in presets, otherwise the source file name.
    pub source: String,
}

#[derive(Debug, Deserialize)]
struct CustomAgent {
    id: String,
    name: String,
    #[serde(default)]
    description: String,
    #[serde(default)]
    system: Option<String>,
    policy: String,
    #[serde(default)]
    max_iterations: Option<usize>,
    #[serde(default)]
    provider: Option<String>,
    #[serde(default)]
    model: Option<String>,
}

/// The built-in presets, in presentation order.
pub fn builtin_profiles() -> Vec<AgentProfile> {
    [
        AgentPreset::Build,
        AgentPreset::Plan,
        AgentPreset::Explore,
        AgentPreset::Review,
        AgentPreset::Verify,
        AgentPreset::Deep,
    ]
    .into_iter()
    .map(|preset| AgentProfile {
        id: preset.as_str().to_owned(),
        name: preset.name().to_owned(),
        description: preset.description().to_owned(),
        system: None,
        prompt_prefix: preset.prompt_prefix().to_owned(),
        policy: preset.tool_access(),
        max_iterations: None,
        provider: None,
        model: None,
        built_in: true,
        source: "builtin".to_owned(),
    })
    .collect()
}

/// Loads the built-in presets and merges the user-defined ones on top.
pub fn load(home: &Path) -> Vec<AgentProfile> {
    let mut profiles = builtin_profiles();
    for custom in custom_profiles(&home.join(AGENTS_DIR)) {
        if custom.id == DEEP_ID {
            continue;
        }
        match profiles.iter_mut().find(|profile| profile.id == custom.id) {
            Some(existing) => *existing = custom,
            None => profiles.push(custom),
        }
    }
    profiles
}

/// Finds a preset by id.
pub fn find<'a>(profiles: &'a [AgentProfile], id: &str) -> Option<&'a AgentProfile> {
    profiles
        .iter()
        .find(|profile| profile.id.eq_ignore_ascii_case(id))
}

/// The wire name of a tool access level.
pub fn policy_label(policy: ToolAccess) -> &'static str {
    match policy {
        ToolAccess::Full => "full",
        ToolAccess::ReadOnly => "read-only",
        ToolAccess::VerifyOnly => "verify-only",
    }
}

fn custom_profiles(directory: &Path) -> Vec<AgentProfile> {
    let Ok(entries) = fs::read_dir(directory) else {
        return Vec::new();
    };
    let mut files: Vec<_> = entries
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .filter(|path| {
            path.extension()
                .is_some_and(|extension| extension == "toml")
        })
        .collect();
    files.sort();

    let mut profiles = Vec::new();
    for path in files {
        match load_custom(&path) {
            Ok(profile) => profiles.push(profile),
            Err(error) => eprintln!("agent: ignoring {}: {error}", path.display()),
        }
    }
    profiles
}

fn load_custom(path: &Path) -> Result<AgentProfile, String> {
    let text = fs::read_to_string(path).map_err(|error| error.to_string())?;
    let custom: CustomAgent = toml::from_str(&text).map_err(|error| error.to_string())?;
    if !valid_id(&custom.id) {
        return Err(format!("invalid agent id: {}", custom.id));
    }
    if custom.id == AgentPreset::Deep.as_str() {
        return Err("the built-in `deep` preset cannot be replaced".to_owned());
    }
    let policy = match custom.policy.as_str() {
        "full" => ToolAccess::Full,
        "read-only" => ToolAccess::ReadOnly,
        "verify-only" => ToolAccess::VerifyOnly,
        other => return Err(format!("unknown policy: {other}")),
    };
    let source = path
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_else(|| custom.id.clone());
    Ok(AgentProfile {
        id: custom.id,
        name: custom.name,
        description: custom.description,
        system: custom.system,
        prompt_prefix: String::new(),
        policy,
        max_iterations: custom.max_iterations.filter(|value| *value > 0),
        provider: custom.provider.filter(|value| !value.trim().is_empty()),
        model: custom.model.filter(|value| !value.trim().is_empty()),
        built_in: false,
        source,
    })
}

fn valid_id(id: &str) -> bool {
    !id.is_empty()
        && id
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
}

#[cfg(test)]
mod tests;
