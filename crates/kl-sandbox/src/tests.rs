use super::*;

#[test]
fn appcontainer_backend_name_round_trips() {
    let config: SandboxConfig = serde_json::from_str(r#"{"backend":"appcontainer"}"#).unwrap();
    assert_eq!(config.backend, SandboxBackendKind::AppContainer);
    assert_eq!(
        serde_json::to_string(&config.backend).unwrap(),
        "\"appcontainer\""
    );
}

#[test]
fn policy_mode_is_available() {
    let manager = SandboxManager::policy_only();
    assert!(manager.status().available);
    assert_eq!(manager.status().backend, "policy");
    assert!(manager.ensure_process_execution().is_ok());
    assert!(!manager.status().capabilities.os_isolation);
}

#[test]
fn strict_policy_is_fail_closed() {
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::Policy,
        ..SandboxConfig::default()
    });
    assert!(!manager.status().available);
    assert!(manager.status().strict);
    assert!(manager.ensure_process_execution().is_err());
}

#[test]
fn strict_auto_is_fail_closed_or_uses_os_backend() {
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::Auto,
        mxc_command: Some("definitely-not-a-real-mxc-command".to_owned()),
        probe_timeout_ms: 500,
    });
    if manager.status().available {
        assert_ne!(manager.status().backend, "policy");
        assert!(manager.ensure_process_execution().is_ok());
    } else {
        assert!(manager.ensure_process_execution().is_err());
        assert!(manager.status().detail.contains("fail"));
    }
}

#[test]
fn z_enumeration_failure_is_classified() {
    assert!(is_z_failure(
        "MXC launcher: enumerate MXC volume Z:\\: network name no longer available"
    ));
    assert!(!is_z_failure("all good"));
}

#[test]
fn sensitive_paths_cover_env_and_credentials() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::write(temp.path().join(".env"), "secret").unwrap();
    std::fs::write(temp.path().join("service.key"), "secret").unwrap();
    let paths = sensitive_paths(temp.path());
    assert!(paths.iter().any(|path| path.ends_with(".env")));
    assert!(paths.iter().any(|path| path.ends_with("service.key")));
}

#[test]
fn enforce_rejects_outside_writes_and_sensitive_reads() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    std::fs::write(workspace.join(".env"), "secret").unwrap();
    let manager = SandboxManager::policy_only();
    let policy = SandboxPolicy::workspace(&workspace);
    let outside = temp.path().join("outside.txt");
    let write = SandboxRequest {
        kind: SandboxViolationKind::WriteOutsideWorkspace,
        resource: outside.display().to_string(),
    };
    assert!(manager.enforce(&write, &policy).unwrap_err().is_violation());
    let sensitive = SandboxRequest {
        kind: SandboxViolationKind::ReadSensitivePath,
        resource: ".env".to_owned(),
    };
    assert!(
        manager
            .enforce(&sensitive, &policy)
            .unwrap_err()
            .is_violation()
    );
}

#[cfg(windows)]
#[test]
fn appcontainer_blocks_outside_write_and_sensitive_read() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    std::fs::write(workspace.join(".env"), "TOP_SECRET").unwrap();
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::AppContainer,
        ..SandboxConfig::default()
    });
    if !manager.status().available {
        eprintln!("AppContainer unavailable: {}", manager.status().detail);
        return;
    }
    let policy = SandboxPolicy::workspace(&workspace);
    let cancel = AtomicBool::new(false);

    let mut inside = CommandSpec::new(
        "cmd",
        vec!["/C".to_owned(), "echo hello>sandbox.txt".to_owned()],
        &workspace,
    );
    inside.timeout = Duration::from_secs(20);
    let output = manager.run(&inside, &cancel, &policy).unwrap();
    assert!(output.success(), "{}", output.combined());
    assert!(workspace.join("sandbox.txt").exists());

    let outside = temp.path().join("outside.txt");
    let mut outside_spec = CommandSpec::new(
        "cmd",
        vec![
            "/C".to_owned(),
            format!("echo outside>\"{}\"", outside.display()),
        ],
        &workspace,
    );
    outside_spec.timeout = Duration::from_secs(20);
    let output = manager.run(&outside_spec, &cancel, &policy).unwrap();
    assert!(!output.success(), "outside write unexpectedly succeeded");
    assert!(!outside.exists());

    let mut sensitive = CommandSpec::new(
        "cmd",
        vec!["/C".to_owned(), "more .env".to_owned()],
        &workspace,
    );
    sensitive.timeout = Duration::from_secs(20);
    let output = manager.run(&sensitive, &cancel, &policy).unwrap();
    assert!(!output.success(), "sensitive read unexpectedly succeeded");
    assert!(!output.stdout.contains("TOP_SECRET"));
}

#[cfg(windows)]
#[test]
fn appcontainer_accepts_clear_environment() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::AppContainer,
        ..SandboxConfig::default()
    });
    if !manager.status().available {
        return;
    }
    let policy = SandboxPolicy::workspace(&workspace);
    let mut spec = CommandSpec::new(
        "cmd",
        vec!["/C".to_owned(), "echo child".to_owned()],
        &workspace,
    );
    spec.clear_env = true;
    spec.env.push((
        "SystemRoot".to_owned(),
        std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".to_owned()),
    ));
    spec.env.push(("=C:".to_owned(), "C:\\".to_owned()));

    let output = manager
        .run(&spec, &AtomicBool::new(false), &policy)
        .unwrap();
    assert!(output.success(), "{}", output.combined());
}

#[cfg(windows)]
#[test]
fn appcontainer_spawns_a_job_contained_child() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::AppContainer,
        ..SandboxConfig::default()
    });
    if !manager.status().available {
        return;
    }
    let policy = SandboxPolicy::workspace(&workspace);
    let spec = CommandSpec::new(
        "cmd",
        vec!["/C".to_owned(), "echo child".to_owned()],
        &workspace,
    );
    let mut child = manager.spawn(&spec, &policy).unwrap();
    assert!(child.try_wait().unwrap().is_none());
    assert_eq!(child.wait().unwrap(), 0);
}
