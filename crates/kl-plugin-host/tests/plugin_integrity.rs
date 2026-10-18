use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use kl_plugin_host::{
    IntegrityError, PluginError, PluginHost, PluginManifest, PluginPermissions,
    PluginSecurityConfig,
};
use ring::signature::{Ed25519KeyPair, KeyPair};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::io::Write as _;
use std::path::{Path, PathBuf};

const WASM_TOOL: &str = include_str!("fixtures/wasm-tool/valid.wat");

struct TestKey {
    pair: Ed25519KeyPair,
    public_key: String,
}

fn test_key() -> TestKey {
    let rng = ring::rand::SystemRandom::new();
    let pkcs8 = Ed25519KeyPair::generate_pkcs8(&rng).expect("generate key");
    let pair = Ed25519KeyPair::from_pkcs8(pkcs8.as_ref()).expect("load key");
    let public_key = BASE64.encode(pair.public_key().as_ref());
    TestKey { pair, public_key }
}

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

fn sha256_hex(path: &Path) -> String {
    let digest = Sha256::digest(std::fs::read(path).expect("read plugin file"));
    let mut output = String::with_capacity(64);
    for byte in digest {
        output.push_str(&format!("{byte:02x}"));
    }
    output
}

fn write_manifest(dir: &Path, manifest: &Value) {
    std::fs::write(
        dir.join("manifest.json"),
        serde_json::to_vec_pretty(manifest).expect("serialize manifest"),
    )
    .expect("write manifest");
}

fn sign_manifest(dir: &Path, manifest: &mut Value, key: &TestKey) {
    write_manifest(dir, manifest);
    let loaded = PluginManifest::load(&dir.join("manifest.json")).expect("load manifest");
    let signature = key.pair.sign(&loaded.signing_payload());
    manifest["signature"] = json!({
        "algorithm": "ed25519",
        "value": BASE64.encode(signature.as_ref()),
    });
    write_manifest(dir, manifest);
}

fn sidecar_source(root: &Path, id: &str, version: &str, entrypoint: &str) -> (PathBuf, Value) {
    let dir = root.join(format!("source-{id}-{version}-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).expect("create source");
    std::fs::write(
        dir.join("config.json"),
        format!("{{\"version\":\"{version}\"}}"),
    )
    .expect("write config");
    let manifest = json!({
        "id": id,
        "version": version,
        "api": 1,
        "kind": "tool",
        "entrypoint": entrypoint,
        "runtime": "sidecar",
        "capabilities": ["tool"],
        "permissions": PluginPermissions::default(),
        "tool_name": "fake_tool",
        "tool_parameters": {"type": "object"},
        "config_path": "config.json",
        "health_timeout_ms": 2000,
        "drain_timeout_ms": 500,
    });
    (dir, manifest)
}

fn wasm_source(root: &Path, id: &str, component: &str) -> (PathBuf, Value) {
    let dir = root.join(format!("source-{id}-{}", uuid::Uuid::new_v4()));
    std::fs::create_dir_all(&dir).expect("create source");
    std::fs::write(dir.join("tool.wat"), component).expect("write component");
    let manifest = json!({
        "id": id,
        "version": "1",
        "api": 1,
        "kind": "tool",
        "entrypoint": "tool.wat",
        "runtime": "wasm",
        "capabilities": ["tool"],
        "permissions": PluginPermissions::default(),
        "tool_name": "wasm_tool",
        "tool_parameters": {"type": "object"},
        "health_timeout_ms": 1000,
        "drain_timeout_ms": 500,
        "call_timeout_ms": 1000,
        "memory_limit_mb": 64,
    });
    (dir, manifest)
}

fn set_integrity(manifest: &mut Value, file: &Path) {
    manifest["integrity"] = json!({"sha256": sha256_hex(file)});
}

fn host_with_key(root: &Path, key: &TestKey) -> PluginHost {
    PluginHost::with_security(
        root,
        PluginSecurityConfig {
            require_signature: true,
            public_key: Some(key.public_key.clone()),
        },
    )
    .expect("create plugin host")
}

fn signed_sidecar(root: &Path, id: &str) -> (PathBuf, TestKey, PluginHost) {
    let key = test_key();
    let (dir, mut manifest) = sidecar_source(root, id, "1", &fake_plugin_binary());
    set_integrity(&mut manifest, Path::new(&fake_plugin_binary()));
    sign_manifest(&dir, &mut manifest, &key);
    let host = host_with_key(&root.join("host"), &key);
    (dir, key, host)
}

#[test]
fn signed_sidecar_installs() {
    let temp = tempfile::tempdir().expect("tempdir");
    let (source, _key, host) = signed_sidecar(temp.path(), "signed-tool");
    let info = host
        .install_from_dir(&source)
        .expect("install signed plugin");
    assert!(info.healthy);
    let output = host
        .tool_execute("signed-tool", "fake_tool", &json!({}), temp.path())
        .expect("execute signed plugin");
    assert!(output.output.contains("fake tool 1"));
}

#[test]
fn tampered_sidecar_file_is_rejected() {
    let temp = tempfile::tempdir().expect("tempdir");
    let key = test_key();
    let (dir, mut manifest) = sidecar_source(temp.path(), "tampered-tool", "1", "fake_plugin.exe");
    let plugin = dir.join("fake_plugin.exe");
    std::fs::copy(fake_plugin_binary(), &plugin).expect("copy plugin binary");
    set_integrity(&mut manifest, &plugin);
    sign_manifest(&dir, &mut manifest, &key);
    let mut file = std::fs::OpenOptions::new()
        .append(true)
        .open(&plugin)
        .expect("open plugin");
    file.write_all(b"\ntampered").expect("tamper plugin");
    let host = host_with_key(&temp.path().join("host"), &key);
    let error = host
        .install_from_dir(&dir)
        .expect_err("tampered plugin installs");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::HashMismatch)
    ));
    assert!(host.get("tampered-tool").is_none());
}

#[test]
fn wrong_public_key_is_rejected() {
    let temp = tempfile::tempdir().expect("tempdir");
    let signer = test_key();
    let verifier = test_key();
    let (dir, mut manifest) =
        sidecar_source(temp.path(), "wrong-key-tool", "1", &fake_plugin_binary());
    set_integrity(&mut manifest, Path::new(&fake_plugin_binary()));
    sign_manifest(&dir, &mut manifest, &signer);
    let host = host_with_key(&temp.path().join("host"), &verifier);
    let error = host.install_from_dir(&dir).expect_err("wrong key installs");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::SignatureMismatch)
    ));
    assert!(host.get("wrong-key-tool").is_none());
}

#[test]
fn missing_signature_is_rejected_when_required() {
    let temp = tempfile::tempdir().expect("tempdir");
    let key = test_key();
    let (dir, mut manifest) =
        sidecar_source(temp.path(), "unsigned-tool", "1", &fake_plugin_binary());
    set_integrity(&mut manifest, Path::new(&fake_plugin_binary()));
    write_manifest(&dir, &manifest);
    let host = host_with_key(&temp.path().join("host"), &key);
    let error = host
        .install_from_dir(&dir)
        .expect_err("unsigned plugin installs");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::MissingSignature)
    ));
    assert!(host.get("unsigned-tool").is_none());
}

#[test]
fn security_config_file_can_require_signatures() {
    let temp = tempfile::tempdir().expect("tempdir");
    let key = test_key();
    let root = temp.path().join("host");
    std::fs::create_dir_all(&root).expect("create host root");
    std::fs::write(
        root.join("security.json"),
        serde_json::to_vec(&json!({
            "require_signature": true,
            "public_key": key.public_key,
        }))
        .expect("serialize security config"),
    )
    .expect("write security config");
    let (dir, mut manifest) =
        sidecar_source(temp.path(), "config-tool", "1", &fake_plugin_binary());
    set_integrity(&mut manifest, Path::new(&fake_plugin_binary()));
    write_manifest(&dir, &manifest);
    let host = PluginHost::new(&root).expect("load security config");
    let error = host
        .install_from_dir(&dir)
        .expect_err("unsigned plugin installs");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::MissingSignature)
    ));
}

#[test]
fn unsigned_dev_plugin_still_installs() {
    let temp = tempfile::tempdir().expect("tempdir");
    let (dir, mut manifest) = sidecar_source(temp.path(), "dev-tool", "1", &fake_plugin_binary());
    set_integrity(&mut manifest, Path::new(&fake_plugin_binary()));
    write_manifest(&dir, &manifest);
    let host = PluginHost::new(temp.path().join("host")).expect("create host");
    let info = host
        .install_from_dir(&dir)
        .expect("install unsigned dev plugin");
    assert!(info.healthy);
}

#[test]
fn signed_wasm_tool_installs() {
    let temp = tempfile::tempdir().expect("tempdir");
    let key = test_key();
    let (dir, mut manifest) = wasm_source(temp.path(), "signed-wasm", WASM_TOOL);
    set_integrity(&mut manifest, &dir.join("tool.wat"));
    sign_manifest(&dir, &mut manifest, &key);
    let host = host_with_key(&temp.path().join("host"), &key);
    let info = host.install_from_dir(&dir).expect("install signed wasm");
    assert!(info.healthy);
    let output = host
        .tool_execute("signed-wasm", "wasm_tool", &json!({}), temp.path())
        .expect("execute signed wasm");
    assert!(output.output.contains("wasm-tool-v1"));
}

#[test]
fn tampered_wasm_tool_is_rejected() {
    let temp = tempfile::tempdir().expect("tempdir");
    let key = test_key();
    let (dir, mut manifest) = wasm_source(temp.path(), "tampered-wasm", WASM_TOOL);
    set_integrity(&mut manifest, &dir.join("tool.wat"));
    sign_manifest(&dir, &mut manifest, &key);
    let mut file = std::fs::OpenOptions::new()
        .append(true)
        .open(dir.join("tool.wat"))
        .expect("open component");
    file.write_all(b"\n;; tampered").expect("tamper component");
    let host = host_with_key(&temp.path().join("host"), &key);
    let error = host
        .install_from_dir(&dir)
        .expect_err("tampered wasm installs");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::HashMismatch)
    ));
    assert!(host.get("tampered-wasm").is_none());
}

#[test]
fn reload_keeps_old_instance_when_installed_manifest_is_tampered() {
    let temp = tempfile::tempdir().expect("tempdir");
    let (source, _key, host) = signed_sidecar(temp.path(), "reload-tool");
    host.install_from_dir(&source)
        .expect("install signed plugin");
    let plugin_root = host.installed_root().join("reload-tool");
    let current: Value = serde_json::from_slice(
        &std::fs::read(plugin_root.join("current.json")).expect("read current"),
    )
    .expect("parse current");
    let manifest_path = plugin_root.join(current["manifest_path"].as_str().expect("manifest path"));
    let mut installed: Value =
        serde_json::from_slice(&std::fs::read(&manifest_path).expect("read manifest"))
            .expect("parse manifest");
    installed["integrity"]["sha256"] = json!("0".repeat(64));
    std::fs::write(
        &manifest_path,
        serde_json::to_vec_pretty(&installed).expect("serialize manifest"),
    )
    .expect("tamper manifest");
    let error = host
        .reload("reload-tool")
        .expect_err("tampered reload succeeds");
    assert!(matches!(
        error,
        PluginError::Integrity(IntegrityError::HashMismatch)
    ));
    assert_eq!(host.get("reload-tool").expect("plugin entry").version, "1");
    let output = host
        .tool_execute("reload-tool", "fake_tool", &json!({}), temp.path())
        .expect("old instance keeps serving");
    assert!(output.output.contains("fake tool 1"));
}
