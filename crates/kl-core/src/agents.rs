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
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn write_agent(directory: &Path, file: &str, content: &str) {
        fs::create_dir_all(directory).unwrap();
        fs::write(directory.join(file), content).unwrap();
    }

    #[test]
    fn builtin_profiles_cover_every_preset() {
        let profiles = builtin_profiles();
        let ids: Vec<_> = profiles.iter().map(|profile| profile.id.as_str()).collect();
        assert_eq!(
            ids,
            vec!["build", "plan", "explore", "review", "verify", "deep"]
        );
        assert!(profiles.iter().all(|profile| profile.built_in));
    }

    #[test]
    fn custom_preset_overrides_the_builtin_with_the_same_id() {
        let home = tempfile::tempdir().unwrap();
        write_agent(
            &home.path().join(AGENTS_DIR),
            "plan.toml",
            r#"
id = "plan"
name = "Custom Plan"
description = "custom"
system = "custom system"
policy = "full"
max_iterations = 3
provider = "openai"
model = "gpt-5"
"#,
        );

        let profiles = load(home.path());
        let plan = find(&profiles, "plan").unwrap();
        assert_eq!(plan.name, "Custom Plan");
        assert_eq!(plan.system.as_deref(), Some("custom system"));
        assert_eq!(plan.policy, ToolAccess::Full);
        assert_eq!(plan.max_iterations, Some(3));
        assert_eq!(plan.provider.as_deref(), Some("openai"));
        assert_eq!(plan.model.as_deref(), Some("gpt-5"));
        assert!(!plan.built_in);
        assert_eq!(profiles.len(), 6);
    }

    #[test]
    fn custom_preset_is_appended_and_keeps_other_builtins() {
        let home = tempfile::tempdir().unwrap();
        write_agent(
            &home.path().join(AGENTS_DIR),
            "custom.toml",
            r#"
id = "custom"
name = "Custom"
policy = "verify-only"
"#,
        );

        let profiles = load(home.path());
        assert_eq!(profiles.len(), 7);
        let custom = find(&profiles, "custom").unwrap();
        assert_eq!(custom.policy, ToolAccess::VerifyOnly);
        assert_eq!(custom.description, "");
        assert!(find(&profiles, "build").unwrap().built_in);
    }

    #[test]
    fn invalid_preset_files_are_skipped() {
        let home = tempfile::tempdir().unwrap();
        let directory = home.path().join(AGENTS_DIR);
        write_agent(
            &directory,
            "bad-policy.toml",
            "id = \"x\"\nname = \"X\"\npolicy = \"nope\"\n",
        );
        write_agent(
            &directory,
            "bad-id.toml",
            "id = \"Bad Id\"\nname = \"X\"\npolicy = \"full\"\n",
        );
        write_agent(&directory, "broken.toml", "not = [toml\n");
        write_agent(&directory, "ignored.txt", "ignored");

        let profiles = load(home.path());
        assert_eq!(profiles.len(), 6);
        assert!(find(&profiles, "x").is_none());
    }

    #[test]
    fn policy_labels_match_the_wire_names() {
        assert_eq!(policy_label(ToolAccess::Full), "full");
        assert_eq!(policy_label(ToolAccess::ReadOnly), "read-only");
        assert_eq!(policy_label(ToolAccess::VerifyOnly), "verify-only");
    }

    #[test]
    fn missing_directory_yields_builtins_only() {
        let home = tempfile::tempdir().unwrap();
        let profiles = load(home.path());
        assert_eq!(profiles.len(), 6);
        assert!(profiles.iter().all(|profile| profile.built_in));
    }

    #[test]
    fn custom_file_without_required_fields_is_rejected() {
        let home = tempfile::tempdir().unwrap();
        let directory = home.path().join(AGENTS_DIR);
        write_agent(
            &directory,
            "no-name.toml",
            "id = \"x\"\npolicy = \"full\"\n",
        );
        write_agent(&directory, "no-policy.toml", "id = \"y\"\nname = \"Y\"\n");
        assert_eq!(load(home.path()).len(), 6);
    }

    #[test]
    fn profiles_are_ordered_with_builtins_first() {
        let home = tempfile::tempdir().unwrap();
        write_agent(
            &home.path().join(AGENTS_DIR),
            "zzz.toml",
            "id = \"zzz\"\nname = \"Z\"\npolicy = \"full\"\n",
        );
        let profiles = load(home.path());
        assert_eq!(profiles.first().unwrap().id, "build");
        assert_eq!(profiles.last().unwrap().id, "zzz");
    }

    #[test]
    fn empty_agent_directory_is_handled() {
        let home = tempfile::tempdir().unwrap();
        let directory: PathBuf = home.path().join(AGENTS_DIR);
        fs::create_dir_all(&directory).unwrap();
        assert_eq!(load(home.path()).len(), 6);
    }
}
