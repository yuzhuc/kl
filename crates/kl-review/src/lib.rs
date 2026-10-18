//! Deterministic diff review pipeline used by `kl review`.
//!
//! Local scanners are the source of truth for fixtures. The LLM contribution is
//! optional and is merged only after parsing a structured finding array.

use regex::Regex;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReviewMode {
    Quick,
    Standard,
    Strict,
}

impl ReviewMode {
    pub fn min_confidence(self) -> f32 {
        match self {
            Self::Quick => 0.80,
            Self::Standard => 0.50,
            Self::Strict => 0.0,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Quick => "quick",
            Self::Standard => "standard",
            Self::Strict => "strict",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Severity {
    Critical,
    High,
    Medium,
    Low,
    Info,
}

impl Severity {
    fn rank(self) -> u8 {
        match self {
            Self::Critical => 5,
            Self::High => 4,
            Self::Medium => 3,
            Self::Low => 2,
            Self::Info => 1,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Critical => "critical",
            Self::High => "high",
            Self::Medium => "medium",
            Self::Low => "low",
            Self::Info => "info",
        }
    }

    fn parse(value: &str) -> Self {
        match value.to_ascii_lowercase().as_str() {
            "critical" => Self::Critical,
            "high" => Self::High,
            "medium" => Self::Medium,
            "low" => Self::Low,
            _ => Self::Info,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct Finding {
    pub rule: String,
    pub source: String,
    pub file: String,
    pub line: u32,
    pub severity: Severity,
    pub confidence: f32,
    pub evidence: String,
    pub recommendation: String,
    pub introduced: bool,
    pub low_confidence: bool,
}

#[derive(Debug, Clone)]
pub struct AddedLine {
    pub number: u32,
    pub text: String,
}

#[derive(Debug, Clone)]
pub struct ChangedFile {
    pub path: String,
    pub added: Vec<AddedLine>,
    pub full_text: String,
}

#[derive(Debug, Clone)]
pub struct ReviewContext {
    pub workspace: PathBuf,
    pub base: Option<String>,
    pub diff: String,
    pub changed_files: Vec<ChangedFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ReviewReport {
    pub mode: String,
    pub summary: String,
    pub changed_files: usize,
    pub findings: Vec<Finding>,
}

#[derive(Debug, thiserror::Error)]
pub enum ReviewError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("git error: {0}")]
    Git(String),
    #[error("review parse error: {0}")]
    Parse(String),
}
pub fn collect_context(
    workspace: impl AsRef<Path>,
    base: Option<String>,
) -> Result<ReviewContext, ReviewError> {
    let workspace = workspace.as_ref().to_path_buf();
    let range = base.clone().unwrap_or_else(|| "HEAD".to_owned());
    let _ = git(&workspace, &["update-index", "--refresh"]);
    let diff = git(
        &workspace,
        &["diff", "--no-ext-diff", "--unified=0", &range],
    )?;
    let mut changed_files = parse_diff(&workspace, &diff);
    let untracked = git(&workspace, &["ls-files", "--others", "--exclude-standard"])?;
    let mut diff = diff;
    for raw in untracked
        .lines()
        .map(str::trim)
        .filter(|line| !line.is_empty())
    {
        let path = raw.replace('\\', "/");
        if changed_files.iter().any(|file| file.path == path) {
            continue;
        }
        let full_path = workspace.join(&path);
        let Ok(text) = std::fs::read_to_string(&full_path) else {
            continue;
        };
        diff.push_str(&format!("\n--- /dev/null\n+++ b/{path}\n"));
        let mut added = Vec::new();
        for (index, line) in text.lines().enumerate() {
            diff.push('+');
            diff.push_str(line);
            diff.push('\n');
            added.push(AddedLine {
                number: (index + 1) as u32,
                text: line.to_owned(),
            });
        }
        changed_files.push(ChangedFile {
            path,
            added,
            full_text: text,
        });
    }
    Ok(ReviewContext {
        workspace,
        base,
        diff,
        changed_files,
    })
}

fn parse_diff(workspace: &Path, diff: &str) -> Vec<ChangedFile> {
    let mut files = Vec::new();
    let mut current: Option<usize> = None;
    let mut new_line = 0u32;
    for line in diff.lines() {
        if let Some(path) = line.strip_prefix("+++ b/") {
            let path = path.to_owned();
            let full_text = std::fs::read_to_string(workspace.join(&path)).unwrap_or_default();
            files.push(ChangedFile {
                path,
                added: Vec::new(),
                full_text,
            });
            current = Some(files.len() - 1);
            continue;
        }
        if line.starts_with("+++ ") {
            current = None;
            continue;
        }
        if line.starts_with("@@ ") {
            new_line = parse_new_line(line).unwrap_or(new_line);
            continue;
        }
        let Some(index) = current else {
            continue;
        };
        if let Some(text) = line.strip_prefix('+') {
            if !line.starts_with("+++") {
                files[index].added.push(AddedLine {
                    number: new_line,
                    text: text.to_owned(),
                });
                new_line += 1;
            }
        } else if line.starts_with(' ') {
            new_line += 1;
        }
    }
    files
}

fn parse_new_line(line: &str) -> Option<u32> {
    let plus = line.split_whitespace().find(|part| part.starts_with('+'))?;
    let start = plus.trim_start_matches('+').split(',').next()?;
    start.parse().ok()
}

fn git(workspace: &Path, args: &[&str]) -> Result<String, ReviewError> {
    let output = Command::new("git")
        .arg("-C")
        .arg(workspace)
        .args(args)
        .output()
        .map_err(|error| ReviewError::Git(format!("cannot start git: {error}")))?;
    if !output.status.success() {
        let detail = String::from_utf8_lossy(&output.stderr).trim().to_owned();
        return Err(ReviewError::Git(if detail.is_empty() {
            format!("git exited with {}", output.status)
        } else {
            detail
        }));
    }
    Ok(String::from_utf8_lossy(&output.stdout).into_owned())
}
pub fn scan_local(context: &ReviewContext) -> Vec<Finding> {
    let mut findings = Vec::new();
    let openai = Regex::new(r"sk-[A-Za-z0-9_-]{20,}").expect("openai regex");
    let aws = Regex::new(r"AKIA[0-9A-Z]{16}").expect("aws regex");
    let generic =
        Regex::new(r#"(?i)(api[_-]?key|secret|token|password)\s*[:=]\s*["'][^"']{12,}["']"#)
            .expect("generic secret regex");
    let function = Regex::new(r"\bpub\s+fn\s+([A-Za-z_][A-Za-z0-9_]*)").expect("function regex");

    for file in &context.changed_files {
        for line in &file.added {
            let text = &line.text;
            if text.contains(".unwrap()") || text.contains(".expect(") {
                findings.push(finding(
                    "debug-unwrap",
                    "rules",
                    &file.path,
                    line.number,
                    Severity::Medium,
                    0.85,
                    text,
                    "Return a typed error or provide an explicit fallback instead of unwrapping.",
                ));
            }
            if text.contains("unsafe") {
                findings.push(finding(
                    "unsafe-block",
                    "rules",
                    &file.path,
                    line.number,
                    Severity::High,
                    0.80,
                    text,
                    "Keep unsafe code behind a documented invariant and add focused tests.",
                ));
            }
            if text.contains("panic!") || text.contains("todo!") || text.contains("unimplemented!")
            {
                findings.push(finding(
                    "panic-path",
                    "rules",
                    &file.path,
                    line.number,
                    Severity::Medium,
                    0.80,
                    text,
                    "Replace panic/todo paths with recoverable errors or complete the branch.",
                ));
            }
            if text.contains("Command::new(") {
                findings.push(finding(
                    "command-injection",
                    "rules",
                    &file.path,
                    line.number,
                    Severity::Medium,
                    0.65,
                    text,
                    "Use an argument array and validate untrusted command/program input.",
                ));
            }
            if (text.contains("SELECT") || text.contains("INSERT")) && text.contains("format!") {
                findings.push(finding(
                    "sql-injection",
                    "rules",
                    &file.path,
                    line.number,
                    Severity::High,
                    0.85,
                    text,
                    "Use parameterized queries instead of interpolating values into SQL.",
                ));
            }
            if let Some(secret) = openai.find(text) {
                findings.push(finding(
                    "secret-openai",
                    "secret-scan",
                    &file.path,
                    line.number,
                    Severity::Critical,
                    0.99,
                    &redact(secret.as_str()),
                    "Remove the key, rotate it, and load credentials from a secret store.",
                ));
            }
            if let Some(secret) = aws.find(text) {
                findings.push(finding(
                    "secret-aws",
                    "secret-scan",
                    &file.path,
                    line.number,
                    Severity::Critical,
                    0.99,
                    &redact(secret.as_str()),
                    "Remove the access key, rotate it, and use an IAM role or secret store.",
                ));
            }
            if let Some(secret) = generic.find(text) {
                findings.push(finding(
                    "secret-generic",
                    "secret-scan",
                    &file.path,
                    line.number,
                    Severity::High,
                    0.90,
                    &redact(secret.as_str()),
                    "Move secrets to environment/credential files and rotate exposed values.",
                ));
            }
        }
    }
    for file in &context.changed_files {
        if file.path.ends_with("Cargo.toml") {
            for line in &file.added {
                if line.text.contains("= \"*\"") || line.text.contains("version = \"*\"") {
                    findings.push(finding(
                        "wildcard-dependency",
                        "dependency",
                        &file.path,
                        line.number,
                        Severity::Medium,
                        0.75,
                        &line.text,
                        "Pin the dependency version or use a bounded semver requirement.",
                    ));
                }
            }
        }
        if file.path.ends_with(".rs") && !file.path.starts_with("tests/") {
            let has_test =
                file.full_text.contains("#[test]") || file.full_text.contains("mod tests");
            let stem = Path::new(&file.path)
                .file_stem()
                .and_then(|value| value.to_str())
                .unwrap_or("file");
            let integration_test = context
                .workspace
                .join("tests")
                .join(format!("{stem}.rs"))
                .exists();
            if !has_test && !integration_test {
                findings.push(finding(
                    "missing-test",
                    "test-gap",
                    &file.path,
                    1,
                    Severity::Medium,
                    0.65,
                    "changed Rust source has no adjacent #[test] or tests/<stem>.rs",
                    "Add a focused regression test for the changed behavior.",
                ));
            }
        }
    }

    let mut definitions = Vec::new();
    for file in &context.changed_files {
        for (index, line) in file.full_text.lines().enumerate() {
            let Some(captures) = function.captures(line) else {
                continue;
            };
            let name = captures[1].to_owned();
            let risky = file.full_text.contains("Command::new(")
                || file.full_text.contains("unsafe")
                || file.full_text.contains("SELECT")
                || file.full_text.contains("INSERT");
            if risky {
                definitions.push((name, file.path.clone(), (index + 1) as u32));
            }
        }
    }
    for (name, definition_file, definition_line) in definitions {
        let call = Regex::new(&format!(r"\b{}\s*\(", regex::escape(&name))).expect("call regex");
        for file in &context.changed_files {
            if file.path == definition_file {
                continue;
            }
            for (index, line) in file.full_text.lines().enumerate() {
                if call.is_match(line) {
                    findings.push(finding(
                        "cross-file-command-injection",
                        "cross-file",
                        &file.path,
                        (index + 1) as u32,
                        Severity::High,
                        0.80,
                        &format!("caller {line}; definition {definition_file}:{definition_line}"),
                        "Validate the untrusted value at the boundary and avoid passing it to a command/SQL sink.",
                    ));
                }
            }
        }
    }

    if context
        .changed_files
        .iter()
        .any(|file| file.path.ends_with(".rs"))
    {
        findings.push(finding(
            "suggest-cargo-test",
            "verification",
            context
                .changed_files
                .iter()
                .find(|file| file.path.ends_with(".rs"))
                .map(|file| file.path.as_str())
                .unwrap_or(""),
            1,
            Severity::Info,
            0.90,
            "Rust changes detected",
            "Run cargo test (and cargo check/clippy for type and lint coverage).",
        ));
    }
    if context
        .changed_files
        .iter()
        .any(|file| file.path.ends_with(".cs"))
    {
        findings.push(finding(
            "suggest-dotnet-test",
            "verification",
            context
                .changed_files
                .iter()
                .find(|file| file.path.ends_with(".cs"))
                .map(|file| file.path.as_str())
                .unwrap_or(""),
            1,
            Severity::Info,
            0.90,
            "C# changes detected",
            "Run dotnet build and dotnet test; add a focused test for the changed path.",
        ));
    }
    findings
}

pub fn runtime_findings(records: &[Value]) -> Vec<Finding> {
    let mut findings = Vec::new();
    for record in records {
        let denied = record.get("decision").and_then(Value::as_str) == Some("deny");
        let error = record
            .get("error")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let sandbox_violation = error.to_ascii_lowercase().contains("sandbox");
        if !denied && !sandbox_violation {
            continue;
        }
        let tool = record
            .get("tool")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let resource = record.get("resource").and_then(Value::as_str).unwrap_or("");
        let id = record
            .get("id")
            .and_then(Value::as_str)
            .unwrap_or("runtime-audit");
        findings.push(finding(
            "runtime-audit-deny",
            "runtime-audit",
            resource,
            0,
            Severity::High,
            1.0,
            &format!("{id}: {tool} {resource} {error}"),
            "Resolve the policy/sandbox denial or explicitly document why it is expected.",
        ));
    }
    findings
}

pub fn model_prompt(context: &ReviewContext, mode: ReviewMode) -> String {
    let diff = truncate(&context.diff, 80_000);
    format!(
        "You are a code review model. Review only the supplied diff.\n\
         Mode: {}\n\
         Return ONLY a JSON array. Each item must have: rule, file, line, severity \
         (critical|high|medium|low|info), confidence (0..1), evidence, recommendation, \
         and optionally introduced (boolean). Focus on security bugs, correctness, \
         missing tests, and cross-file risks. Do not invent files or lines.\n\n\
         Diff:\n{diff}",
        mode.as_str()
    )
}

pub fn parse_model_findings(text: &str) -> Result<Vec<Finding>, ReviewError> {
    let start = text
        .find('[')
        .ok_or_else(|| ReviewError::Parse("no JSON array".to_owned()))?;
    let end = text
        .rfind(']')
        .ok_or_else(|| ReviewError::Parse("no JSON array".to_owned()))?;
    if end < start {
        return Err(ReviewError::Parse("invalid JSON array bounds".to_owned()));
    }
    let values: Vec<Value> = serde_json::from_str(&text[start..=end])
        .map_err(|error| ReviewError::Parse(error.to_string()))?;
    let mut findings = Vec::new();
    for value in values {
        let file = value.get("file").and_then(Value::as_str).unwrap_or("");
        let line = value.get("line").and_then(Value::as_u64).unwrap_or(0) as u32;
        let rule = value
            .get("rule")
            .and_then(Value::as_str)
            .unwrap_or("llm-finding");
        let severity = Severity::parse(
            value
                .get("severity")
                .and_then(Value::as_str)
                .unwrap_or("info"),
        );
        let confidence = value
            .get("confidence")
            .and_then(Value::as_f64)
            .unwrap_or(0.5) as f32;
        findings.push(finding(
            rule,
            "llm",
            file,
            line,
            severity,
            confidence.clamp(0.0, 1.0),
            value.get("evidence").and_then(Value::as_str).unwrap_or(""),
            value
                .get("recommendation")
                .and_then(Value::as_str)
                .unwrap_or("Review the cited change."),
        ));
    }
    Ok(findings)
}
pub fn finalize(mode: ReviewMode, changed_files: usize, findings: Vec<Finding>) -> ReviewReport {
    let mut deduped: BTreeMap<String, Finding> = BTreeMap::new();
    for mut item in findings {
        item.low_confidence = item.confidence < 0.5;
        if item.confidence < mode.min_confidence() {
            continue;
        }
        let key = format!(
            "{}|{}|{}|{}",
            item.rule, item.file, item.line, item.evidence
        );
        if let Some(existing) = deduped.get_mut(&key) {
            existing.confidence = existing.confidence.max(item.confidence);
            existing.introduced |= item.introduced;
            existing.low_confidence = existing.confidence < 0.5;
        } else {
            deduped.insert(key, item);
        }
    }
    let mut findings: Vec<Finding> = deduped.into_values().collect();
    findings.sort_by(|left, right| {
        right
            .severity
            .rank()
            .cmp(&left.severity.rank())
            .then_with(|| left.file.cmp(&right.file))
            .then_with(|| left.line.cmp(&right.line))
            .then_with(|| left.rule.cmp(&right.rule))
    });
    let critical = findings
        .iter()
        .filter(|item| item.severity == Severity::Critical)
        .count();
    let high = findings
        .iter()
        .filter(|item| item.severity == Severity::High)
        .count();
    let medium = findings
        .iter()
        .filter(|item| item.severity == Severity::Medium)
        .count();
    let low = findings
        .iter()
        .filter(|item| item.severity == Severity::Low)
        .count();
    let info = findings
        .iter()
        .filter(|item| item.severity == Severity::Info)
        .count();
    ReviewReport {
        mode: mode.as_str().to_owned(),
        summary: format!(
            "{changed_files} changed files; {critical} critical, {high} high, {medium} medium, {low} low, {info} info"
        ),
        changed_files,
        findings,
    }
}

pub fn llm_warning(evidence: &str, recommendation: &str) -> Finding {
    finding(
        "llm-review-unavailable",
        "llm",
        "",
        0,
        Severity::Low,
        0.40,
        evidence,
        recommendation,
    )
}
#[allow(clippy::too_many_arguments)]
fn finding(
    rule: &str,
    source: &str,
    file: &str,
    line: u32,
    severity: Severity,
    confidence: f32,
    evidence: &str,
    recommendation: &str,
) -> Finding {
    Finding {
        rule: rule.to_owned(),
        source: source.to_owned(),
        file: file.to_owned(),
        line,
        severity,
        confidence,
        evidence: truncate(evidence.trim(), 500),
        recommendation: recommendation.to_owned(),
        introduced: true,
        low_confidence: confidence < 0.5,
    }
}

fn redact(value: &str) -> String {
    let visible: String = value.chars().take(6).collect();
    format!("{visible}<redacted>")
}

fn truncate(text: &str, limit: usize) -> String {
    if text.len() <= limit {
        return text.to_owned();
    }
    let mut end = limit;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}\n... [review input truncated]", &text[..end])
}

#[cfg(test)]
mod tests {
    use super::*;

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
}
