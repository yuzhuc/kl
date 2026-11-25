use super::*;

fn prompt_context() -> ReviewContext {
    ReviewContext {
        workspace: PathBuf::from("/tmp/workspace"),
        base: None,
        diff: "- old\n+new".to_owned(),
        changed_files: vec![ChangedFile {
            path: "src/lib.rs".to_owned(),
            added: Vec::new(),
            full_text: "fn full_file() {}".to_owned(),
        }],
    }
}

#[test]
fn prompt_appends_instructions_and_can_use_full_files() {
    let context = prompt_context();
    let diff_prompt = model_prompt(&context, ReviewMode::Standard, Some("check unsafe"), false);
    assert!(diff_prompt.contains("+new"));
    assert!(diff_prompt.contains("Additional review instructions:\ncheck unsafe"));
    assert!(!diff_prompt.contains("fn full_file()"));

    let full_prompt = model_prompt(&context, ReviewMode::Strict, None, true);
    assert!(full_prompt.contains("--- src/lib.rs ---"));
    assert!(full_prompt.contains("fn full_file() {}"));
    assert!(!full_prompt.contains("+new"));
}

#[test]
fn parses_structured_model_findings() {
    let findings = parse_model_findings(
            r#"[{"rule":"auth-bypass","file":"src/auth.rs","line":12,"severity":"high","confidence":0.9,"evidence":"admin check","recommendation":"fix"}]"#,
        )
        .unwrap();
    assert_eq!(findings.len(), 1);
    assert_eq!(findings[0].severity, Severity::High);
    assert_eq!(findings[0].source, "llm");
}

#[test]
fn strict_keeps_low_confidence_and_quick_filters_it() {
    let item = finding(
        "low",
        "rules",
        "src/a.rs",
        1,
        Severity::Low,
        0.2,
        "evidence",
        "fix",
    );
    assert!(
        finalize(ReviewMode::Quick, 1, vec![item.clone()])
            .findings
            .is_empty()
    );
    let strict = finalize(ReviewMode::Strict, 1, vec![item]);
    assert_eq!(strict.findings.len(), 1);
    assert!(strict.findings[0].low_confidence);
}
