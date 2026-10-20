#![cfg(any(target_os = "linux", target_os = "macos"))]

use kl_exec::CommandOutput;
use kl_exec::CommandSpec;
use kl_sandbox::{SandboxBackendKind, SandboxConfig, SandboxManager, SandboxMode, SandboxPolicy};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::time::Duration;

static NEXT_ID: AtomicU32 = AtomicU32::new(0);

fn home_dir() -> PathBuf {
    std::env::var_os("HOME")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir)
}

fn test_workspace() -> tempfile::TempDir {
    tempfile::Builder::new()
        .prefix("kl-sandbox-")
        .tempdir_in(home_dir())
        .expect("workspace under HOME")
}

fn outside_path() -> PathBuf {
    home_dir().join(format!(
        "kl-sandbox-outside-{}-{}",
        std::process::id(),
        NEXT_ID.fetch_add(1, Ordering::Relaxed)
    ))
}

fn strict_manager() -> Option<SandboxManager> {
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::Auto,
        ..SandboxConfig::default()
    });
    let status = manager.status();
    println!(
        "sandbox backend={} available={} detail={}",
        status.backend, status.available, status.detail
    );
    if status.available {
        return Some(manager);
    }
    let detail = status.detail.clone();
    if std::env::var_os("KL_REQUIRE_OS_SANDBOX").is_some() {
        report("OS sandbox unavailable", &detail);
        panic!("OS sandbox unavailable: {detail}");
    }
    eprintln!("skipping OS sandbox test: {detail}");
    None
}

fn run(
    manager: &SandboxManager,
    script: &str,
    cwd: PathBuf,
    policy: &SandboxPolicy,
) -> CommandOutput {
    let mut spec = CommandSpec::new("/bin/sh", vec!["-c".to_owned(), script.to_owned()], cwd);
    spec.timeout = Duration::from_secs(20);
    spec.clear_env = true;
    spec.env = vec![(
        "PATH".to_owned(),
        "/usr/bin:/bin:/usr/sbin:/sbin".to_owned(),
    )];
    manager
        .run(&spec, &AtomicBool::new(false), policy)
        .expect("sandbox run")
}

fn expect_success(label: &str, output: &CommandOutput) {
    if !output.success() {
        fail(label, output);
    }
}

fn expect_failure(label: &str, output: &CommandOutput) {
    if output.success() {
        fail(label, output);
    }
}

fn fail(label: &str, output: &CommandOutput) -> ! {
    report(
        label,
        &format!("status={:?} {}", output.status, output.combined()),
    );
    panic!("{label}: status={:?} {}", output.status, output.combined());
}

/// GitHub only shows annotations without a login on this repository, so put
/// the failing command output where the run summary can see it.
fn report(label: &str, detail: &str) {
    if std::env::var_os("CI").is_some() {
        let detail = detail.replace('\n', " ");
        println!("::error title={label}::{detail}");
    }
}

#[test]
fn os_sandbox_allows_workspace_writes_and_blocks_outside_writes() {
    let Some(manager) = strict_manager() else {
        return;
    };
    let workspace = test_workspace();
    let policy = SandboxPolicy::workspace(workspace.path());

    let inside = workspace.path().join("inside.txt");
    let output = run(
        &manager,
        "printf ok > inside.txt",
        workspace.path().to_path_buf(),
        &policy,
    );
    expect_success("workspace write", &output);
    assert_eq!(std::fs::read_to_string(&inside).unwrap_or_default(), "ok");

    let outside = outside_path();
    let output = run(
        &manager,
        &format!("printf no > {}", outside.display()),
        workspace.path().to_path_buf(),
        &policy,
    );
    expect_failure("outside write", &output);
    assert!(!outside.exists(), "outside file must not be created");
}

#[test]
fn os_sandbox_blocks_network_unless_allowed() {
    let Some(manager) = strict_manager() else {
        return;
    };
    let workspace = test_workspace();
    let mut policy = SandboxPolicy::workspace(workspace.path());
    policy.allow_network = false;
    let output = run(
        &manager,
        "curl -fsS --max-time 3 http://1.1.1.1/ >/dev/null",
        workspace.path().to_path_buf(),
        &policy,
    );
    expect_failure("network blocked", &output);

    policy.allow_network = true;
    let output = run(
        &manager,
        "curl -fsS --max-time 3 http://1.1.1.1/ >/dev/null",
        workspace.path().to_path_buf(),
        &policy,
    );
    expect_success("network allowed", &output);
}
