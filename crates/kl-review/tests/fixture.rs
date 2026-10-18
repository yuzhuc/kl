use kl_review::{
    ReviewMode, collect_context, finalize, parse_model_findings, runtime_findings, scan_local,
};
use std::path::Path;
use std::process::Command;

fn copy_dir(source: &Path, target: &Path) {
    std::fs::create_dir_all(target).unwrap();
    for entry in std::fs::read_dir(source).unwrap() {
        let entry = entry.unwrap();
        if entry.file_name() == ".git" {
            continue;
        }
        let destination = target.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &destination);
        } else {
            std::fs::copy(entry.path(), destination).unwrap();
        }
    }
}

fn git(workspace: &Path, args: &[&str]) {
    let status = Command::new("git")
        .arg("-C")
        .arg(workspace)
        .args([
            "-c",
            "user.email=review@example.test",
            "-c",
            "user.name=review",
        ])
        .args(args)
        .status()
        .unwrap();
    assert!(status.success(), "git {:?} failed", args);
}

#[test]
fn fixture_finds_known_bug_secret_missing_test_and_cross_file_risk() {
    let temp = tempfile::tempdir().unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let base = root.join("tests/fixtures/review-repo/base");
    let changed = root.join("tests/fixtures/review-repo/changed");
    copy_dir(&base, temp.path());
    git(temp.path(), &["init"]);
    git(temp.path(), &["add", "."]);
    git(temp.path(), &["commit", "-m", "base"]);
    copy_dir(&changed, temp.path());
    touch_dir(temp.path());

    let context = collect_context(temp.path(), None).unwrap();
    let mut findings = scan_local(&context);
    findings.extend(runtime_findings(&[serde_json::json!({
        "id": "audit-deny-1",
        "decision": "deny",
        "tool": "shell",
        "resource": "rm -rf /",
        "error": "policy denied"
    })]));
    findings.extend(
        parse_model_findings(
            r#"[{"rule":"model-auth","file":"src/handler.rs","line":2,"severity":"high","confidence":0.9,"evidence":"untrusted input reaches auth boundary","recommendation":"validate input"}]"#,
        )
        .unwrap(),
    );
    let report = finalize(ReviewMode::Strict, context.changed_files.len(), findings);
    let rules: Vec<&str> = report
        .findings
        .iter()
        .map(|finding| finding.rule.as_str())
        .collect();
    for expected in [
        "debug-unwrap",
        "secret-openai",
        "wildcard-dependency",
        "missing-test",
        "cross-file-command-injection",
        "runtime-audit-deny",
        "model-auth",
    ] {
        assert!(
            rules.contains(&expected),
            "missing {expected}; got {rules:?}"
        );
    }
    assert!(
        report
            .findings
            .iter()
            .any(|finding| finding.source == "llm")
    );
    assert!(
        report
            .findings
            .iter()
            .all(|finding| finding.file.is_empty() || !finding.file.starts_with('/'))
    );
}

fn touch_dir(path: &Path) {
    for entry in std::fs::read_dir(path).unwrap() {
        let entry = entry.unwrap();
        if entry.file_name() == ".git" {
            continue;
        }
        if entry.file_type().unwrap().is_dir() {
            touch_dir(&entry.path());
        } else {
            let bytes = std::fs::read(entry.path()).unwrap();
            std::fs::write(entry.path(), bytes).unwrap();
        }
    }
}
