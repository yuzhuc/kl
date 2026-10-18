use kl_plugin_host::{PluginError, PluginHost, PluginPermissions, WasmError};
use serde_json::json;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const VALID_COMPONENT: &str = include_str!("fixtures/wasm-tool/valid.wat");

fn workspace(root: &Path) -> PathBuf {
    let workspace = root.join("workspace");
    std::fs::create_dir_all(workspace.join("src")).unwrap();
    std::fs::write(workspace.join("src/lib.rs"), "pub fn value() -> u8 { 1 }\n").unwrap();
    workspace
}

fn source(
    root: &Path,
    id: &str,
    version: &str,
    component: &str,
    permissions: PluginPermissions,
    memory_limit_mb: u64,
    call_timeout_ms: u64,
) -> PathBuf {
    let dir = root.join(format!("source-{id}-{version}-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    std::fs::write(dir.join("tool.wat"), component).unwrap();
    let manifest = json!({
        "id": id,
        "version": version,
        "api": 1,
        "kind": "tool",
        "entrypoint": "tool.wat",
        "runtime": "wasm",
        "capabilities": ["tool"],
        "permissions": permissions,
        "tool_name": "wasm_tool",
        "tool_parameters": {"type": "object"},
        "health_timeout_ms": 1000,
        "drain_timeout_ms": 500,
        "call_timeout_ms": call_timeout_ms,
        "memory_limit_mb": memory_limit_mb,
    });
    std::fs::write(
        dir.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .unwrap();
    dir
}

fn invalid_source(root: &Path) -> PathBuf {
    let dir = root.join(format!("source-invalid-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    std::fs::write(dir.join("tool.wasm"), b"not a wasm component").unwrap();
    let manifest = json!({
        "id": "invalid-tool",
        "version": "1",
        "api": 1,
        "kind": "tool",
        "entrypoint": "tool.wasm",
        "runtime": "wasm",
        "tool_name": "wasm_tool",
        "tool_parameters": {"type": "object"},
    });
    std::fs::write(
        dir.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .unwrap();
    dir
}

fn escaped(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

fn success_body(length: usize) -> String {
    format!(
        "(call $time)\n      drop\n      (call $log (i32.const 128) (i32.const {length}))\n      (i32.store (i32.const 0) (i32.const 128))\n      (i32.store (i32.const 4) (i32.const {length}))\n      (i32.const 0)"
    )
}

fn component(health: &str, output: &str, execute_body: &str) -> String {
    let health = escaped(health);
    let output = escaped(output);
    let health_len = health.replace("\\\"", "\"").len();
    format!(
        r#"(component
  (import "host-log" (func $host_log (param "message" string)))
  (import "host-time" (func $host_time (result u64)))
  (core module $libc
    (memory (export "memory") 1)
    (func (export "realloc") (param i32 i32 i32 i32) (result i32) i32.const 4096))
  (core instance $libc_i (instantiate $libc))
  (core module $main
    (import "libc" "memory" (memory 1))
    (import "host" "log" (func $log (param i32 i32)))
    (import "host" "time" (func $time (result i64)))
    (data (i32.const 16) "{health}")
    (data (i32.const 128) "{output}")
    (func (export "health") (result i32)
      (call $time)
      drop
      (call $log (i32.const 16) (i32.const {health_len}))
      (i32.store (i32.const 0) (i32.const 16))
      (i32.store (i32.const 4) (i32.const {health_len}))
      (i32.const 0))
    (func (export "execute") (param i32 i32) (result i32)
      {execute_body}))
  (core func $log_lower
    (canon lower (func $host_log) (memory (core memory $libc_i "memory"))))
  (core func $time_lower (canon lower (func $host_time)))
  (core instance $host_i
    (export "log" (func $log_lower))
    (export "time" (func $time_lower)))
  (core instance $main_i
    (instantiate $main
      (with "libc" (instance $libc_i))
      (with "host" (instance $host_i))))
  (func (export "health") (result string)
    (canon lift (core func $main_i "health")
      (memory (core memory $libc_i "memory"))
      (realloc (core func $libc_i "realloc"))))
  (func (export "execute") (param "input" string) (result string)
    (canon lift (core func $main_i "execute")
      (memory (core memory $libc_i "memory"))
      (realloc (core func $libc_i "realloc")))))"#
    )
}

fn outcome(output: &str) -> String {
    json!({
        "success": true,
        "summary": "wasm tool completed",
        "output": output,
    })
    .to_string()
}

fn permissions() -> PluginPermissions {
    PluginPermissions {
        fs_read: vec!["workspace".to_owned()],
        ..PluginPermissions::default()
    }
}

#[test]
fn wasm_tool_valid_execute() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let source = source(
        temp.path(),
        "wasm-tool",
        "1",
        VALID_COMPONENT,
        permissions(),
        64,
        1000,
    );
    let info = host.install_from_dir(&source).unwrap();
    assert!(info.healthy);
    assert_eq!(info.detail.as_deref(), Some("wasm component"));

    let result = host
        .tool_execute("wasm-tool", "wasm_tool", &json!({"value": 7}), &workspace)
        .unwrap();
    assert!(result.success);
    assert_eq!(result.summary, "wasm tool completed");
    assert!(result.output.contains("wasm-tool-v1"));
}

#[test]
fn wasm_health_failure_keeps_previous_version() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let v1 = source(
        temp.path(),
        "wasm-tool",
        "1",
        VALID_COMPONENT,
        permissions(),
        64,
        1000,
    );
    host.install_from_dir(&v1).unwrap();

    let failing = component(
        r#"{"ok":false}"#,
        &outcome("wasm-tool-v2"),
        &success_body(outcome("wasm-tool-v2").len()),
    );
    let v2 = source(
        temp.path(),
        "wasm-tool",
        "2",
        &failing,
        permissions(),
        64,
        1000,
    );
    let error = host.install_from_dir(&v2).unwrap_err();
    assert!(matches!(error, PluginError::Wasm(WasmError::Health(_))));
    assert_eq!(host.get("wasm-tool").unwrap().version, "1");
    let result = host
        .tool_execute("wasm-tool", "wasm_tool", &json!({}), &workspace)
        .unwrap();
    assert!(result.output.contains("wasm-tool-v1"));
}

#[test]
fn wasm_trap_is_normalized_without_stopping_host() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let trapped = component(r#"{"ok":true}"#, &outcome("unused"), "unreachable");
    let source = source(
        temp.path(),
        "trap-tool",
        "1",
        &trapped,
        permissions(),
        64,
        1000,
    );
    host.install_from_dir(&source).unwrap();

    let error = host
        .tool_execute("trap-tool", "wasm_tool", &json!({}), &workspace)
        .unwrap_err();
    assert!(matches!(error, PluginError::Wasm(WasmError::Trap(_))));
    assert_eq!(host.list().len(), 1);
    assert!(!host.get("trap-tool").unwrap().healthy);
}

#[test]
fn wasm_call_timeout_is_enforced() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let timeout = component(
        r#"{"ok":true}"#,
        &outcome("unused"),
        "(loop $spin (br $spin)) (i32.const 0)",
    );
    let source = source(
        temp.path(),
        "slow-tool",
        "1",
        &timeout,
        permissions(),
        64,
        100,
    );
    host.install_from_dir(&source).unwrap();

    let started = Instant::now();
    let error = host
        .tool_execute("slow-tool", "wasm_tool", &json!({}), &workspace)
        .unwrap_err();
    assert!(matches!(error, PluginError::Wasm(WasmError::Timeout(_))));
    assert!(started.elapsed() < Duration::from_secs(2));
}

#[test]
fn wasm_invalid_component_is_rejected() {
    let temp = tempfile::tempdir().unwrap();
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let source = invalid_source(temp.path());
    let error = host.install_from_dir(&source).unwrap_err();
    assert!(matches!(
        error,
        PluginError::Wasm(WasmError::InvalidComponent(_))
    ));
    assert!(host.get("invalid-tool").is_none());
}

#[test]
fn wasm_tool_still_honors_plugin_permissions() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let source = source(
        temp.path(),
        "denied-tool",
        "1",
        VALID_COMPONENT,
        PluginPermissions::default(),
        64,
        1000,
    );
    host.install_from_dir(&source).unwrap();

    let error = host
        .tool_execute(
            "denied-tool",
            "wasm_tool",
            &json!({"path": "src/lib.rs"}),
            &workspace,
        )
        .unwrap_err();
    assert!(matches!(error, PluginError::PermissionDenied(_)));
}

#[test]
fn wasm_tool_hot_reload_switches_after_health() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = workspace(temp.path());
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let v1 = source(
        temp.path(),
        "wasm-tool",
        "1",
        VALID_COMPONENT,
        permissions(),
        64,
        1000,
    );
    host.install_from_dir(&v1).unwrap();

    let output = outcome("wasm-tool-v2");
    let v2_component = component(r#"{"ok":true}"#, &output, &success_body(output.len()));
    let v2 = source(
        temp.path(),
        "wasm-tool",
        "2",
        &v2_component,
        permissions(),
        64,
        1000,
    );
    let info = host.install_from_dir(&v2).unwrap();
    assert_eq!(info.version, "2");

    let result = host
        .tool_execute("wasm-tool", "wasm_tool", &json!({}), &workspace)
        .unwrap();
    assert!(result.output.contains("wasm-tool-v2"));
}

#[test]
fn wasm_memory_limit_is_enforced() {
    let temp = tempfile::tempdir().unwrap();
    let host = PluginHost::new(temp.path().join("host")).unwrap();
    let oversized = VALID_COMPONENT.replace(
        "(memory (export \"memory\") 1)",
        "(memory (export \"memory\") 32)",
    );
    let source = source(
        temp.path(),
        "memory-tool",
        "1",
        &oversized,
        permissions(),
        1,
        1000,
    );
    let error = host.install_from_dir(&source).unwrap_err();
    assert!(matches!(error, PluginError::Wasm(_)));
    assert!(host.get("memory-tool").is_none());
}
