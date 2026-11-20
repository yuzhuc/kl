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
