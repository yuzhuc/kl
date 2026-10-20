#![cfg(any(target_os = "linux", target_os = "macos"))]

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
    if manager.status().available {
        return Some(manager);
    }
    let detail = manager.status().detail.clone();
    if std::env::var_os("KL_REQUIRE_OS_SANDBOX").is_some() {
        panic!("OS sandbox unavailable: {detail}");
    }
    eprintln!("skipping OS sandbox test: {detail}");
    None
}

fn run(manager: &SandboxManager, script: &str, cwd: PathBuf, policy: &SandboxPolicy) -> i32 {
    let mut spec = CommandSpec::new("/bin/sh", vec!["-c".to_owned(), script.to_owned()], cwd);
    spec.timeout = Duration::from_secs(20);
    spec.clear_env = true;
    spec.env = vec![(
        "PATH".to_owned(),
        "/usr/bin:/bin:/usr/sbin:/sbin".to_owned(),
    )];
    let output = manager
        .run(&spec, &AtomicBool::new(false), policy)
        .expect("sandbox run");
    output.status.unwrap_or(-1)
}

#[test]
fn os_sandbox_allows_workspace_writes_and_blocks_outside_writes() {
    let Some(manager) = strict_manager() else {
        return;
    };
    let workspace = test_workspace();
    let policy = SandboxPolicy::workspace(workspace.path());

    let inside = workspace.path().join("inside.txt");
    let status = run(
        &manager,
        "printf ok > inside.txt",
        workspace.path().to_path_buf(),
        &policy,
    );
    assert_eq!(status, 0, "workspace write should succeed");
    assert_eq!(std::fs::read_to_string(&inside).unwrap_or_default(), "ok");

    let outside = outside_path();
    let status = run(
        &manager,
        &format!("printf no > {}", outside.display()),
        workspace.path().to_path_buf(),
        &policy,
    );
    assert_ne!(status, 0, "write outside the sandbox roots must fail");
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
    let blocked = run(
        &manager,
        "curl -fsS --max-time 3 http://1.1.1.1/ >/dev/null",
        workspace.path().to_path_buf(),
        &policy,
    );
    assert_ne!(blocked, 0, "network must be blocked without a grant");

    policy.allow_network = true;
    let allowed = run(
        &manager,
        "curl -fsS --max-time 3 http://1.1.1.1/ >/dev/null",
        workspace.path().to_path_buf(),
        &policy,
    );
    assert_eq!(allowed, 0, "network grant should permit outbound traffic");
}
