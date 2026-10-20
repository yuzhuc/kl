use kl_plugin_host::{
    PluginError, PluginHost, PluginPermissions, SidecarError, SidecarProvider, SidecarTool,
};
use kl_provider::{CancellationToken, ChatEvent, ChatMessage, ChatRequest, Provider};
#[cfg(windows)]
use kl_sandbox::{SandboxBackendKind, SandboxConfig, SandboxManager, SandboxMode};
use kl_tools::Tool;
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::sync::Arc;

fn fake_plugin_binary() -> String {
    option_env!("CARGO_BIN_EXE_fake_plugin")
        .map(str::to_owned)
        .unwrap_or_else(|| {
            let exe = std::env::current_exe().expect("test executable path");
            let debug = exe
                .parent()
                .and_then(Path::parent)
                .expect("debug directory");
            debug
                .join(if cfg!(windows) {
                    "fake_plugin.exe"
                } else {
                    "fake_plugin"
                })
                .to_string_lossy()
                .into_owned()
        })
}

fn make_source(
    root: &Path,
    id: &str,
    version: &str,
    kind: &str,
    config: Value,
    permissions: PluginPermissions,
    health_timeout_ms: u64,
) -> PathBuf {
    let dir = root.join(format!("source-{id}-{version}-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    let mut manifest = json!({
        "id": id,
        "version": version,
        "api": 1,
        "kind": kind,
        "entrypoint": fake_plugin_binary(),
        "runtime": "sidecar",
        "capabilities": if kind == "provider" { vec!["chat", "streaming"] } else { vec!["tool"] },
        "permissions": permissions,
        "config_path": "config.json",
        "health_timeout_ms": health_timeout_ms,
        "drain_timeout_ms": 500,
    });
    if kind == "tool" {
        manifest["tool_name"] = json!("fake_tool");
        manifest["tool_parameters"] = json!({"type": "object"});
    } else {
        manifest["models"] = json!(["fake-model"]);
    }
    std::fs::write(
        dir.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .unwrap();
    std::fs::write(
        dir.join("config.json"),
        serde_json::to_vec_pretty(&config).unwrap(),
    )
    .unwrap();
    dir
}

fn workspace(root: &Path) -> PathBuf {
    let workspace = root.join("workspace");
    std::fs::create_dir_all(workspace.join("src")).unwrap();
    std::fs::write(workspace.join("src/lib.rs"), "pub fn value() -> u8 { 1 }\n").unwrap();
    std::fs::write(root.join("secret.txt"), "outside").unwrap();
    workspace
}

#[test]
fn tool_hot_upgrade_env_network_and_path_permissions() {
    unsafe {
        std::env::set_var("KL_PLUGIN_TEST_ALLOWED", "visible-secret");
        std::env::set_var("KL_PLUGIN_TEST_DENIED", "hidden-secret");
    }
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let permissions = PluginPermissions {
        network: vec!["example.com".to_owned()],
        env: vec!["KL_PLUGIN_TEST_ALLOWED".to_owned()],
        fs_read: vec!["workspace".to_owned()],
        fs_write: Vec::new(),
    };
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let source = make_source(
        temp.path(),
        "fake-tool",
        "1",
        "tool",
        json!({"version": "1"}),
        permissions.clone(),
        2000,
    );
    host.install_from_dir(&source).unwrap();

    let allowed = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"env_name": "KL_PLUGIN_TEST_ALLOWED"}),
            &workspace,
        )
        .unwrap();
    assert!(allowed.output.contains("visible-secret"));
    let denied = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"env_name": "KL_PLUGIN_TEST_DENIED"}),
            &workspace,
        )
        .unwrap();
    assert!(denied.output.contains("<missing>"));

    let network_ok = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"url": "https://example.com/v1"}),
            &workspace,
        )
        .unwrap();
    assert!(network_ok.success);
    let network_denied = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"url": "https://evil.example.net"}),
            &workspace,
        )
        .unwrap_err();
    assert!(matches!(network_denied, PluginError::PermissionDenied(_)));

    host.tool_execute(
        "fake-tool",
        "fake_tool",
        &json!({"path": "src/lib.rs"}),
        &workspace,
    )
    .unwrap();
    let path_denied = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"path": "../secret.txt"}),
            &workspace,
        )
        .unwrap_err();
    assert!(matches!(path_denied, PluginError::PermissionDenied(_)));
    let write_denied = host
        .tool_execute(
            "fake-tool",
            "fake_tool",
            &json!({"path": "src/lib.rs", "write": true}),
            &workspace,
        )
        .unwrap_err();
    assert!(matches!(write_denied, PluginError::PermissionDenied(_)));

    let adapter = SidecarTool::new(host.clone(), "fake-tool").unwrap();
    assert_eq!(adapter.name(), "fake_tool");

    let source_v2 = make_source(
        temp.path(),
        "fake-tool",
        "2",
        "tool",
        json!({"version": "2"}),
        permissions,
        2000,
    );
    host.install_from_dir(&source_v2).unwrap();
    assert_eq!(host.get("fake-tool").unwrap().version, "2");
    let upgraded = host
        .tool_execute("fake-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(upgraded.output.contains("fake tool 2"));
}

#[test]
fn provider_hot_upgrade_keeps_the_same_adapter() {
    let temp = tempfile::tempdir().unwrap();
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let permissions = PluginPermissions::default();
    let source_v1 = make_source(
        temp.path(),
        "fake-provider",
        "1",
        "provider",
        json!({"provider_text": "provider one"}),
        permissions.clone(),
        2000,
    );
    host.install_from_dir(&source_v1).unwrap();
    let provider = SidecarProvider::new(host.clone(), "fake-provider");
    let request = ChatRequest::new("fake-model", vec![ChatMessage::user("hello")]);
    let mut deltas = String::new();
    let first = provider
        .stream_chat(&request, &CancellationToken::new(), &mut |event| {
            if let ChatEvent::TextDelta(text) = event {
                deltas.push_str(&text);
            }
        })
        .unwrap();
    assert_eq!(first.text, "provider one");
    assert_eq!(deltas, "provider one");

    let source_v2 = make_source(
        temp.path(),
        "fake-provider",
        "2",
        "provider",
        json!({"provider_text": "provider two"}),
        permissions,
        2000,
    );
    host.install_from_dir(&source_v2).unwrap();
    let second = provider
        .stream_chat(&request, &CancellationToken::new(), &mut |_| {})
        .unwrap();
    assert_eq!(second.text, "provider two");
    assert_eq!(host.get("fake-provider").unwrap().version, "2");
}

#[test]
fn plugin_crash_is_isolated_and_reported() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let source = make_source(
        temp.path(),
        "crash-tool",
        "1",
        "tool",
        json!({"crash_on_execute": true}),
        PluginPermissions::default(),
        2000,
    );
    host.install_from_dir(&source).unwrap();

    let error = host
        .tool_execute("crash-tool", "fake_tool", &json!({}), &workspace)
        .unwrap_err();
    assert!(matches!(error, PluginError::Sidecar(_)));
    let info = host.get("crash-tool").unwrap();
    assert!(!info.healthy);
    assert_eq!(host.list().len(), 1);
}

#[test]
fn slow_startup_fails_and_keeps_previous_version() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let permissions = PluginPermissions {
        fs_read: vec!["workspace".to_owned()],
        ..PluginPermissions::default()
    };
    let v1 = make_source(
        temp.path(),
        "slow-tool",
        "1",
        "tool",
        json!({"version": "1"}),
        permissions.clone(),
        2000,
    );
    host.install_from_dir(&v1).unwrap();
    let v2 = make_source(
        temp.path(),
        "slow-tool",
        "2",
        "tool",
        json!({"version": "2", "startup_delay_ms": 1000}),
        permissions,
        200,
    );
    let error = host.install_from_dir(&v2).unwrap_err();
    assert!(matches!(error, PluginError::Sidecar(_)));
    assert_eq!(host.get("slow-tool").unwrap().version, "1");
    let output = host
        .tool_execute("slow-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(output.output.contains("fake tool 1"));
}

#[test]
fn failed_health_check_rolls_back_to_previous_version() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let permissions = PluginPermissions {
        fs_read: vec!["workspace".to_owned()],
        ..PluginPermissions::default()
    };
    let v1 = make_source(
        temp.path(),
        "health-tool",
        "1",
        "tool",
        json!({"version": "1"}),
        permissions.clone(),
        2000,
    );
    host.install_from_dir(&v1).unwrap();
    let v2 = make_source(
        temp.path(),
        "health-tool",
        "2",
        "tool",
        json!({"version": "2", "fail_health": true}),
        permissions,
        2000,
    );
    assert!(host.install_from_dir(&v2).is_err());
    assert_eq!(host.get("health-tool").unwrap().version, "1");
    let output = host
        .tool_execute("health-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(output.output.contains("fake tool 1"));
}

#[test]
fn capability_token_mismatch_prevents_installation() {
    let temp = tempfile::tempdir().unwrap();
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let source = make_source(
        temp.path(),
        "token-tool",
        "1",
        "tool",
        json!({"expected_token": "not-the-host-token"}),
        PluginPermissions::default(),
        2000,
    );
    let error = host.install_from_dir(&source).unwrap_err();
    match error {
        PluginError::Sidecar(SidecarError::Rpc { message, .. }) => {
            assert!(message.contains("capability token"));
        }
        other => panic!("unexpected error: {other}"),
    }
    assert!(host.get("token-tool").is_none());
}

#[test]
fn transactional_config_update_rolls_back() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let permissions = PluginPermissions {
        fs_read: vec!["workspace".to_owned()],
        ..PluginPermissions::default()
    };
    let source = make_source(
        temp.path(),
        "config-tool",
        "1",
        "tool",
        json!({"version": "1"}),
        permissions,
        2000,
    );
    host.install_from_dir(&source).unwrap();

    let error = host
        .update_config("config-tool", json!({"version": "2", "fail_health": true}))
        .unwrap_err();
    assert!(matches!(error, PluginError::Sidecar(_)));
    let old = host
        .tool_execute("config-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(old.output.contains("fake tool 1"));

    host.update_config("config-tool", json!({"version": "2"}))
        .unwrap();
    let new = host
        .tool_execute("config-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(new.output.contains("fake tool 2"));
}

#[test]
fn disable_drains_and_enable_restarts() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = Arc::new(PluginHost::new(temp.path().join("host")).unwrap());
    let permissions = PluginPermissions {
        fs_read: vec!["workspace".to_owned()],
        ..PluginPermissions::default()
    };
    let source = make_source(
        temp.path(),
        "toggle-tool",
        "1",
        "tool",
        json!({"version": "1"}),
        permissions,
        2000,
    );
    host.install_from_dir(&source).unwrap();
    let disabled = host.disable("toggle-tool").unwrap();
    assert!(!disabled.enabled);
    assert!(
        !host
            .tool_execute("toggle-tool", "fake_tool", &json!({}), &workspace)
            .is_ok()
    );
    let enabled = host.enable("toggle-tool").unwrap();
    assert!(enabled.enabled);
    assert!(
        host.tool_execute("toggle-tool", "fake_tool", &json!({}), &workspace)
            .unwrap()
            .success
    );
}

#[cfg(windows)]
#[test]
fn malicious_sidecar_cannot_read_outside_workspace() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let secret = temp.path().join("secret.txt");
    std::fs::write(&secret, "TOP_SECRET").unwrap();
    let manager = SandboxManager::probe(&SandboxConfig {
        mode: SandboxMode::Strict,
        backend: SandboxBackendKind::AppContainer,
        ..SandboxConfig::default()
    });
    if !manager.status().available {
        return;
    }
    let source = make_source(
        temp.path(),
        "escape-tool",
        "1",
        "tool",
        json!({"escape_read": secret}),
        PluginPermissions {
            fs_read: vec!["workspace".to_owned()],
            ..PluginPermissions::default()
        },
        2000,
    );
    let host =
        Arc::new(PluginHost::with_sandbox(temp.path().join("host"), Arc::new(manager)).unwrap());
    host.install_from_dir(&source).unwrap();
    let output = host
        .tool_execute("escape-tool", "fake_tool", &json!({}), &workspace)
        .unwrap();
    assert!(!output.output.contains("TOP_SECRET"), "{}", output.output);
    assert!(
        output.output.contains("escape-read-denied"),
        "{}",
        output.output
    );
}
