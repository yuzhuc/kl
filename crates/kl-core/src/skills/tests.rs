use super::*;
use kl_policy::{PolicyConfig, PolicyEngine};
use kl_sandbox::SandboxManager;
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::time::Duration;

fn write_skill(root: &Path, directory: &str, content: &str) -> PathBuf {
    let path = root.join(directory).join(SKILL_FILE);
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(&path, content).unwrap();
    path
}

fn skill_content(name: &str, description: &str, body: &str) -> String {
    format!("---\nname: {name}\ndescription: {description}\n---\n{body}")
}

#[test]
fn discovers_home_and_workspace_with_workspace_override() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    write_skill(
        &home.path().join("skills"),
        "alpha",
        &skill_content("alpha", "home alpha", "HOME BODY"),
    );
    write_skill(
        &workspace.path().join(".kl/skills"),
        "alpha",
        &skill_content("alpha", "workspace alpha", "WORKSPACE BODY"),
    );

    let skills = discover(home.path(), workspace.path());
    let alpha = skills.iter().find(|skill| skill.name == "alpha").unwrap();
    assert_eq!(alpha.description, "workspace alpha");
    assert_eq!(skills.len(), 1);
}

#[test]
fn uses_directory_name_and_skips_missing_frontmatter() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    write_skill(
        &home.path().join("skills"),
        "from-directory",
        "---\ndescription: fallback name\n---\nBODY",
    );
    write_skill(
        &home.path().join("skills"),
        "missing",
        "description: no frontmatter\nBODY",
    );

    let skills = discover(home.path(), workspace.path());
    assert_eq!(skills.len(), 1);
    assert_eq!(skills[0].name, "from-directory");
}

#[test]
fn installs_the_builtin_skill_once() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    install_system_skills(home.path());

    let skills = discover(home.path(), workspace.path());
    let skill = skills
        .iter()
        .find(|skill| skill.name == "skill-creator")
        .expect("built-in skill");
    assert!(skill.description.contains("instruction file"));

    let path = home.path().join("skills/.system/skill-creator/SKILL.md");
    std::fs::write(
        &path,
        "---
name: skill-creator
description: edited
---
EDITED
",
    )
    .unwrap();
    install_system_skills(home.path());
    assert!(std::fs::read_to_string(&path).unwrap().contains("EDITED"));
}

#[test]
fn summary_excludes_body_and_path_and_is_bounded() {
    let skill = SkillSummary {
        name: "alpha".to_owned(),
        description: "x".repeat(SUMMARY_MAX_BYTES + 100),
        path: PathBuf::from("C:/secret/path/SKILL.md"),
    };
    let summary = summary_prompt(&[skill]);
    assert!(summary.len() <= SUMMARY_MAX_BYTES);
    assert!(summary.starts_with("alpha: "));
    assert!(!summary.contains("secret/path"));
    assert!(!summary.contains("BODY"));
}

fn context<'a>(policy: &'a PolicyEngine, cancel: &'a AtomicBool) -> ToolContext<'a> {
    ToolContext {
        policy,
        cancel,
        sandbox: Arc::new(SandboxManager::policy_only()),
        timeout: Duration::from_secs(5),
        max_output_bytes: 64 * 1024,
    }
}

#[test]
fn skill_tool_loads_body_by_name() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    write_skill(
        &workspace.path().join(".kl/skills"),
        "alpha",
        &skill_content("alpha", "Alpha description", "Run the checks.\n"),
    );
    let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let tool = SkillTool {
        home: home.path().to_path_buf(),
    };

    let request = tool
        .policy_request_for(workspace.path(), &json!({"name": "alpha"}))
        .unwrap();
    assert_eq!(request.action, Action::Read);
    assert_eq!(request.resource, ".kl/skills/alpha");
    assert_eq!(
        policy.evaluate(request.action, &request.resource).effect,
        kl_policy::Effect::Allow
    );

    let outcome = tool
        .execute(&context(&policy, &cancel), &json!({"name": "alpha"}))
        .unwrap();
    assert!(outcome.output.starts_with("<skill_content name=\"alpha\">"));
    assert!(outcome.output.contains("Run the checks."));
    assert!(!outcome.output.contains("description: Alpha description"));
}

#[test]
fn load_body_loads_trusted_home_skill() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    write_skill(
        &home.path().join("skills"),
        "alpha",
        &skill_content("alpha", "Alpha description", "HOME BODY"),
    );

    assert_eq!(
        load_body(home.path(), workspace.path(), "alpha").unwrap(),
        "HOME BODY"
    );
}

#[test]
fn load_body_truncates_long_body_on_char_boundary() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    let body = format!("{}TAIL", "界".repeat(BODY_MAX_BYTES));
    write_skill(
        &home.path().join("skills"),
        "big",
        &skill_content("big", "Big description", &body),
    );

    let loaded = load_body(home.path(), workspace.path(), "big").unwrap();
    assert!(loaded.ends_with("[skill truncated]"));
    assert!(!loaded.contains("TAIL"));
    assert!(loaded.len() <= BODY_MAX_BYTES + "\n[skill truncated]".len());
}

#[test]
fn skill_tool_loads_home_skill_body() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    write_skill(
        &home.path().join("skills"),
        "alpha",
        &skill_content("alpha", "Alpha description", "HOME BODY"),
    );
    let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let tool = SkillTool {
        home: home.path().to_path_buf(),
    };

    let request = tool
        .policy_request_for(workspace.path(), &json!({"name": "alpha"}))
        .unwrap();
    assert_eq!(
        policy.evaluate(request.action, &request.resource).effect,
        kl_policy::Effect::Allow
    );
    let outcome = tool
        .execute(&context(&policy, &cancel), &json!({"name": "alpha"}))
        .unwrap();
    assert!(outcome.output.contains("HOME BODY"));
}

#[test]
fn skill_tool_rejects_unknown_and_invalid_names() {
    let home = tempfile::tempdir().unwrap();
    let workspace = tempfile::tempdir().unwrap();
    let policy = PolicyEngine::new(workspace.path(), PolicyConfig::default()).unwrap();
    let cancel = AtomicBool::new(false);
    let tool = SkillTool {
        home: home.path().to_path_buf(),
    };

    let unknown = tool
        .execute(&context(&policy, &cancel), &json!({"name": "missing"}))
        .unwrap_err();
    assert!(unknown.to_string().contains("unknown skill: missing"));

    let invalid = tool
        .execute(&context(&policy, &cancel), &json!({"name": "../missing"}))
        .unwrap_err();
    assert!(invalid.to_string().contains("invalid skill name"));
}
