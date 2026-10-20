#![cfg(windows)]

use kl_exec::CommandSpec;
use kl_sandbox::{SandboxBackendKind, SandboxConfig, SandboxManager, SandboxMode, SandboxPolicy};
use std::io::ErrorKind;
use std::net::TcpListener;
use std::process::Command;
use std::sync::atomic::AtomicBool;
use std::time::Duration;

const PROBE: &str = env!("CARGO_BIN_EXE_kl-net-probe");

fn strict_manager() -> Option<SandboxManager> {
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::AppContainer,
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

/// GitHub only shows annotations without a login on this repository, so put
/// the failing command output where the run summary can see it.
fn report(label: &str, detail: &str) {
    if std::env::var_os("CI").is_some() {
        let detail = detail.replace('\n', " ");
        println!("::error title={label}::{detail}");
    }
}

#[test]
fn appcontainer_denies_loopback_network() {
    let Some(manager) = strict_manager() else {
        return;
    };
    let workspace = tempfile::tempdir().unwrap();
    let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
    let target = listener.local_addr().unwrap().to_string();

    // Control: the probe must reach the listener outside the sandbox, so a
    // denial inside the sandbox proves isolation and not a broken probe.
    let control = Command::new(PROBE).arg(&target).output().unwrap();
    assert!(
        control.status.success(),
        "probe failed outside the sandbox: {}",
        String::from_utf8_lossy(&control.stderr)
    );
    listener.set_nonblocking(true).unwrap();
    assert!(
        listener.accept().is_ok(),
        "control probe did not reach the listener"
    );

    let mut spec = CommandSpec::new(PROBE, vec![target.clone()], workspace.path());
    spec.timeout = Duration::from_secs(30);
    // Both policy values must be denied: the AppContainer token carries no
    // network capability, so the block is enforced by the OS and not by the
    // policy allowlist alone.
    for allow_network in [false, true] {
        let mut policy = SandboxPolicy::workspace(workspace.path());
        policy.allow_network = allow_network;
        let output = manager
            .run(&spec, &AtomicBool::new(false), &policy)
            .expect("sandbox run");
        assert_eq!(
            output.status,
            Some(1),
            "sandboxed probe must run and fail to connect: {}",
            output.combined()
        );
        assert!(
            output.combined().contains("connect failed"),
            "unexpected probe output: {}",
            output.combined()
        );
        let accepted = listener.accept();
        assert!(accepted.is_err(), "sandboxed probe reached the listener");
        assert_eq!(accepted.unwrap_err().kind(), ErrorKind::WouldBlock);
    }
}
