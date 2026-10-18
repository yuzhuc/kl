mod adapter;
mod manifest;
mod sidecar;
mod wasm;

pub use adapter::{SidecarProvider, SidecarTool};
pub use manifest::{
    ManifestError, PLUGIN_API_VERSION, PluginKind, PluginManifest, PluginPermissions,
};
pub use sidecar::SidecarError;
use sidecar::SidecarProcess;
pub use wasm::WasmError;
use wasm::WasmInstance;

use kl_policy::{PolicyConfig, PolicyEngine};
use kl_provider::{ChatOutcome, ChatRequest};
use kl_sandbox::{
    SandboxError, SandboxManager, SandboxPolicy, SandboxRequest, SandboxViolation,
    SandboxViolationKind,
};
use kl_tools::ToolOutcome;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use walkdir::WalkDir;

#[derive(Debug, thiserror::Error)]
pub enum PluginError {
    #[error("manifest error: {0}")]
    Manifest(#[from] ManifestError),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("sidecar error: {0}")]
    Sidecar(#[from] SidecarError),
    #[error("wasm error: {0}")]
    Wasm(#[from] WasmError),
    #[error("plugin not found: {0}")]
    NotFound(String),
    #[error("plugin disabled: {0}")]
    Disabled(String),
    #[error("plugin kind mismatch: {0}")]
    KindMismatch(String),
    #[error("plugin permission denied: {0}")]
    PermissionDenied(String),
    #[error("plugin protocol error: {0}")]
    Protocol(String),
    #[error("sandbox violation: {0}")]
    Sandbox(#[from] SandboxViolation),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PluginInfo {
    pub id: String,
    pub version: String,
    pub kind: PluginKind,
    pub enabled: bool,
    pub healthy: bool,
    pub tool_name: Option<String>,
    pub models: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

pub(crate) struct SidecarInstance {
    pub(crate) manifest: PluginManifest,
    pub(crate) manifest_path: PathBuf,
    pub(crate) token: String,
    pub(crate) process: Arc<SidecarProcess>,
    in_flight: Arc<AtomicUsize>,
    draining: AtomicBool,
}

impl SidecarInstance {
    fn start(
        manifest: PluginManifest,
        manifest_path: PathBuf,
        env_overrides: &[(String, String)],
        sandbox: &SandboxManager,
    ) -> Result<Self, PluginError> {
        let token = uuid::Uuid::new_v4().to_string();
        let plugin_dir = PluginManifest::manifest_dir(&manifest_path);
        let policy = manifest_policy(&manifest, &manifest_path, &plugin_dir);
        let process = Arc::new(SidecarProcess::spawn(
            &manifest,
            &manifest_path,
            &token,
            env_overrides,
            sandbox,
            &policy,
        )?);
        let timeout = Duration::from_millis(manifest.health_timeout_ms.max(100));
        process.handshake(&manifest, &token, timeout)?;
        process.health(&token, timeout)?;
        Ok(Self {
            manifest,
            manifest_path,
            token,
            process,
            in_flight: Arc::new(AtomicUsize::new(0)),
            draining: AtomicBool::new(false),
        })
    }
    fn healthy(&self) -> bool {
        !self.draining.load(Ordering::Relaxed) && self.process.is_alive()
    }

    fn begin_request(&self) -> Result<InFlightGuard, PluginError> {
        if self.draining.load(Ordering::Relaxed) {
            return Err(PluginError::Disabled(self.manifest.id.clone()));
        }
        if !self.process.is_alive() {
            return Err(PluginError::Sidecar(SidecarError::Exited(
                "plugin process is not running".to_owned(),
            )));
        }
        self.in_flight.fetch_add(1, Ordering::SeqCst);
        Ok(InFlightGuard {
            in_flight: self.in_flight.clone(),
        })
    }

    fn request(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
    ) -> Result<Value, PluginError> {
        let _guard = self.begin_request()?;
        Ok(self.process.request(method, params, timeout)?)
    }

    fn stop(&self, timeout: Duration) {
        self.draining.store(true, Ordering::SeqCst);
        let deadline = Instant::now() + timeout;
        while self.in_flight.load(Ordering::SeqCst) > 0 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        self.process.stop(Duration::from_millis(500));
    }
}

struct InFlightGuard {
    in_flight: Arc<AtomicUsize>,
}

impl Drop for InFlightGuard {
    fn drop(&mut self) {
        self.in_flight.fetch_sub(1, Ordering::SeqCst);
    }
}

#[derive(Clone)]
enum ActiveInstance {
    Sidecar(Arc<SidecarInstance>),
    Wasm(Arc<WasmInstance>),
}

impl ActiveInstance {
    fn manifest(&self) -> &PluginManifest {
        match self {
            Self::Sidecar(instance) => &instance.manifest,
            Self::Wasm(instance) => instance.manifest(),
        }
    }

    fn is_healthy(&self) -> bool {
        match self {
            Self::Sidecar(instance) => instance.healthy(),
            Self::Wasm(instance) => instance.is_healthy(),
        }
    }

    fn stop(&self, timeout: Duration) {
        match self {
            Self::Sidecar(instance) => instance.stop(timeout),
            Self::Wasm(instance) => instance.stop(timeout),
        }
    }

    fn detail(&self) -> String {
        match self {
            Self::Sidecar(instance) => format!("sidecar pid {}", instance.process.pid()),
            Self::Wasm(_) => "wasm component".to_owned(),
        }
    }
}

struct PluginEntry {
    manifest: PluginManifest,
    manifest_path: PathBuf,
    active: Option<ActiveInstance>,
    enabled: bool,
    detail: Option<String>,
}

pub struct PluginHost {
    root: PathBuf,
    entries: Mutex<HashMap<String, PluginEntry>>,
    request_timeout: Duration,
    sandbox: Arc<SandboxManager>,
}

impl PluginHost {
    pub fn new(root: impl AsRef<Path>) -> Result<Self, PluginError> {
        Self::with_sandbox(root, Arc::new(SandboxManager::policy_only()))
    }

    pub fn with_sandbox(
        root: impl AsRef<Path>,
        sandbox: Arc<SandboxManager>,
    ) -> Result<Self, PluginError> {
        let root = root.as_ref().to_path_buf();
        fs::create_dir_all(root.join("installed"))?;
        let host = Self {
            root,
            entries: Mutex::new(HashMap::new()),
            request_timeout: Duration::from_secs(30),
            sandbox,
        };
        host.load_installed();
        Ok(host)
    }

    fn start_instance(
        &self,
        manifest: PluginManifest,
        manifest_path: PathBuf,
        env_overrides: &[(String, String)],
    ) -> Result<ActiveInstance, PluginError> {
        if manifest.is_wasm() {
            return Ok(ActiveInstance::Wasm(Arc::new(WasmInstance::start(
                manifest,
                &manifest_path,
            )?)));
        }
        Ok(ActiveInstance::Sidecar(Arc::new(SidecarInstance::start(
            manifest,
            manifest_path,
            env_overrides,
            &self.sandbox,
        )?)))
    }
    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn installed_root(&self) -> PathBuf {
        self.root.join("installed")
    }

    pub fn list(&self) -> Vec<PluginInfo> {
        let entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let mut infos: Vec<PluginInfo> = entries.values().map(|entry| self.info(entry)).collect();
        infos.sort_by(|left, right| left.id.cmp(&right.id));
        infos
    }

    pub fn get(&self, id: &str) -> Option<PluginInfo> {
        let entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        entries.get(id).map(|entry| self.info(entry))
    }

    pub fn install_from_dir(&self, source: &Path) -> Result<PluginInfo, PluginError> {
        let source_manifest = find_manifest(source).ok_or_else(|| {
            PluginError::NotFound(format!("no manifest found under {}", source.display()))
        })?;
        let manifest = PluginManifest::load(&source_manifest)?;
        let id = manifest.id.clone();
        let version_dir = self.version_dir(&id, &manifest.version);
        if version_dir.exists() {
            fs::remove_dir_all(&version_dir)?;
        }
        copy_dir(source, &version_dir)?;
        let installed_manifest = find_manifest(&version_dir).ok_or_else(|| {
            PluginError::NotFound("installed plugin manifest disappeared".to_owned())
        })?;
        let installed = PluginManifest::load(&installed_manifest)?;

        let new_instance =
            self.start_instance(installed.clone(), installed_manifest.clone(), &[])?;
        self.set_current(&id, &version_dir, &installed_manifest)?;
        let _ = fs::remove_file(self.root.join("installed").join(&id).join(".disabled"));
        let old = {
            let mut entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            if let Some(entry) = entries.get_mut(&id) {
                let old = entry.active.replace(new_instance.clone());
                entry.manifest = installed.clone();
                entry.manifest_path = installed_manifest;
                entry.enabled = true;
                entry.detail = None;
                old
            } else {
                entries.insert(
                    id.clone(),
                    PluginEntry {
                        manifest: installed.clone(),
                        manifest_path: installed_manifest,
                        active: Some(new_instance),
                        enabled: true,
                        detail: None,
                    },
                );
                None
            }
        };
        if let Some(old) = old {
            old.stop(Duration::from_millis(installed.drain_timeout_ms.max(100)));
        }
        Ok(self.get(&id).expect("plugin was just inserted"))
    }

    pub fn reload(&self, id: &str) -> Result<PluginInfo, PluginError> {
        let (manifest, manifest_path, old_active) = {
            let entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            if !entry.enabled {
                return Err(PluginError::Disabled(id.to_owned()));
            }
            (
                entry.manifest.clone(),
                entry.manifest_path.clone(),
                entry.active.clone(),
            )
        };
        let new_instance = self.start_instance(manifest.clone(), manifest_path.clone(), &[])?;
        {
            let mut entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get_mut(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            entry.active = Some(new_instance);
            entry.detail = None;
        }
        if let Some(old) = old_active {
            old.stop(Duration::from_millis(manifest.drain_timeout_ms.max(100)));
        }
        Ok(self.get(id).expect("plugin entry exists"))
    }

    pub fn enable(&self, id: &str) -> Result<PluginInfo, PluginError> {
        let (manifest, manifest_path) = {
            let entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            (entry.manifest.clone(), entry.manifest_path.clone())
        };
        let instance = self.start_instance(manifest, manifest_path, &[])?;
        let marker = self.root.join("installed").join(id).join(".disabled");
        let _ = fs::remove_file(marker);
        let mut entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let entry = entries
            .get_mut(id)
            .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
        entry.active = Some(instance);
        entry.enabled = true;
        entry.detail = None;
        Ok(self.info(entry))
    }

    pub fn disable(&self, id: &str) -> Result<PluginInfo, PluginError> {
        let (active, drain_timeout) = {
            let mut entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get_mut(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            entry.enabled = false;
            entry.detail = Some("disabled by user".to_owned());
            (entry.active.take(), entry.manifest.drain_timeout_ms)
        };
        fs::create_dir_all(self.root.join("installed").join(id))?;
        fs::write(
            self.root.join("installed").join(id).join(".disabled"),
            b"disabled\n",
        )?;
        if let Some(active) = active {
            active.stop(Duration::from_millis(drain_timeout.max(100)));
        }
        Ok(self.get(id).expect("plugin entry exists"))
    }

    pub fn update_config(&self, id: &str, config: Value) -> Result<PluginInfo, PluginError> {
        let (manifest, manifest_path, old_active) = {
            let entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            (
                entry.manifest.clone(),
                entry.manifest_path.clone(),
                entry.active.clone(),
            )
        };
        let config_path = manifest
            .config_path(&manifest_path)
            .unwrap_or_else(|| PluginManifest::manifest_dir(&manifest_path).join("config.json"));
        let previous = fs::read(&config_path).ok();
        write_atomic(&config_path, &serde_json::to_vec_pretty(&config)?)?;

        let new_instance = match self.start_instance(manifest.clone(), manifest_path.clone(), &[]) {
            Ok(instance) => instance,
            Err(error) => {
                restore_file(&config_path, previous.as_deref())?;
                return Err(error);
            }
        };
        {
            let mut entries = self
                .entries
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let entry = entries
                .get_mut(id)
                .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
            entry.active = Some(new_instance);
            entry.detail = None;
        }
        if let Some(old) = old_active {
            old.stop(Duration::from_millis(manifest.drain_timeout_ms.max(100)));
        }
        Ok(self.get(id).expect("plugin entry exists"))
    }

    pub fn provider_available(&self, id: &str) -> (bool, String) {
        match self.get(id) {
            Some(info) if info.kind == PluginKind::Provider && info.enabled => (
                info.healthy,
                info.detail.unwrap_or_else(|| "sidecar provider".to_owned()),
            ),
            Some(info) => (
                false,
                info.detail
                    .unwrap_or_else(|| "provider disabled".to_owned()),
            ),
            None => (false, "provider plugin is not installed".to_owned()),
        }
    }

    pub fn provider_models(&self, id: &str) -> Vec<String> {
        self.get(id).map(|info| info.models).unwrap_or_default()
    }

    pub fn provider_chat(
        &self,
        id: &str,
        request: &ChatRequest,
    ) -> Result<ChatOutcome, PluginError> {
        let instance = self.active_instance(id, PluginKind::Provider)?;
        let ActiveInstance::Sidecar(sidecar) = instance else {
            return Err(PluginError::KindMismatch(format!(
                "plugin `{id}` does not use the sidecar runtime"
            )));
        };
        let result = sidecar.request(
            "provider.chat",
            json!({"token": sidecar.token, "request": request}),
            self.request_timeout,
        )?;
        serde_json::from_value(result).map_err(|error| PluginError::Protocol(error.to_string()))
    }

    pub fn tool_execute(
        &self,
        id: &str,
        tool: &str,
        args: &Value,
        workspace: &Path,
    ) -> Result<ToolOutcome, PluginError> {
        let instance = self.active_instance(id, PluginKind::Tool)?;
        let manifest = instance.manifest();
        if manifest.tool_name() != tool {
            return Err(PluginError::KindMismatch(format!(
                "plugin `{id}` exposes `{}`, not `{tool}`",
                manifest.tool_name()
            )));
        }
        validate_tool_permissions(manifest, args, workspace)?;
        let result = match &instance {
            ActiveInstance::Sidecar(sidecar) => {
                let policy = manifest_policy(&sidecar.manifest, &sidecar.manifest_path, workspace);
                self.sandbox.grant(&policy).map_err(plugin_sandbox_error)?;
                if let Some(raw_path) = args.get("path").and_then(Value::as_str) {
                    let write = args.get("write").and_then(Value::as_bool).unwrap_or(false)
                        || args.get("mode").and_then(Value::as_str) == Some("write");
                    self.sandbox
                        .enforce(
                            &SandboxRequest {
                                kind: if write {
                                    SandboxViolationKind::WriteOutsideWorkspace
                                } else {
                                    SandboxViolationKind::ReadSensitivePath
                                },
                                resource: raw_path.to_owned(),
                            },
                            &policy,
                        )
                        .map_err(plugin_sandbox_error)?;
                }
                sidecar.request(
                    "tool.execute",
                    json!({
                        "token": sidecar.token,
                        "tool": tool,
                        "args": args,
                        "workspace": workspace,
                    }),
                    self.request_timeout,
                )?
            }
            ActiveInstance::Wasm(wasm) => wasm.execute(json!({
                "tool": tool,
                "args": args,
                "workspace": workspace,
            }))?,
        };
        Ok(ToolOutcome {
            success: result
                .get("success")
                .and_then(Value::as_bool)
                .unwrap_or(false),
            summary: result
                .get("summary")
                .and_then(Value::as_str)
                .unwrap_or("plugin tool completed")
                .to_owned(),
            output: result
                .get("output")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned(),
            diff: result
                .get("diff")
                .and_then(Value::as_str)
                .map(str::to_owned),
        })
    }

    pub fn manifest(&self, id: &str) -> Option<PluginManifest> {
        let entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        entries.get(id).map(|entry| entry.manifest.clone())
    }

    fn active_instance(&self, id: &str, kind: PluginKind) -> Result<ActiveInstance, PluginError> {
        let entries = self
            .entries
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        let entry = entries
            .get(id)
            .ok_or_else(|| PluginError::NotFound(id.to_owned()))?;
        if !entry.enabled {
            return Err(PluginError::Disabled(id.to_owned()));
        }
        if entry.manifest.kind != kind {
            return Err(PluginError::KindMismatch(format!(
                "plugin `{id}` is not a {:?}",
                kind
            )));
        }
        entry
            .active
            .clone()
            .ok_or_else(|| PluginError::Disabled(format!("plugin `{id}` has no active instance")))
    }

    fn info(&self, entry: &PluginEntry) -> PluginInfo {
        PluginInfo {
            id: entry.manifest.id.clone(),
            version: entry.manifest.version.clone(),
            kind: entry.manifest.kind,
            enabled: entry.enabled,
            healthy: entry
                .active
                .as_ref()
                .map(|active| active.is_healthy())
                .unwrap_or(false),
            tool_name: entry.manifest.tool_name.clone().or_else(|| {
                (entry.manifest.kind == PluginKind::Tool).then(|| entry.manifest.tool_name())
            }),
            models: entry.manifest.models(),
            detail: entry
                .detail
                .clone()
                .or_else(|| entry.active.as_ref().map(ActiveInstance::detail)),
        }
    }

    fn version_dir(&self, id: &str, version: &str) -> PathBuf {
        self.root
            .join("installed")
            .join(id)
            .join("versions")
            .join(format!("{version}-{}", uuid::Uuid::new_v4()))
    }

    fn set_current(
        &self,
        id: &str,
        version_dir: &Path,
        manifest_path: &Path,
    ) -> Result<(), PluginError> {
        let plugin_root = self.root.join("installed").join(id);
        fs::create_dir_all(&plugin_root)?;
        let relative = manifest_path
            .strip_prefix(&plugin_root)
            .unwrap_or(manifest_path)
            .to_string_lossy()
            .replace('\\', "/");
        let current = plugin_root.join("current.json");
        write_atomic(
            &current,
            &serde_json::to_vec_pretty(&json!({
                "version_dir": version_dir.strip_prefix(&plugin_root).unwrap_or(version_dir)
                    .to_string_lossy().replace('\\', "/"),
                "manifest_path": relative,
            }))?,
        )?;
        Ok(())
    }

    fn load_installed(&self) {
        let installed = self.root.join("installed");
        let Ok(dirs) = fs::read_dir(&installed) else {
            return;
        };
        for dir in dirs.flatten() {
            if !dir.file_type().map(|kind| kind.is_dir()).unwrap_or(false) {
                continue;
            }
            let plugin_root = dir.path();
            let disabled = plugin_root.join(".disabled").exists();
            let manifest_path =
                current_manifest(&plugin_root).or_else(|| find_manifest(&plugin_root));
            let Some(manifest_path) = manifest_path else {
                continue;
            };
            let Ok(manifest) = PluginManifest::load(&manifest_path) else {
                continue;
            };
            let id = manifest.id.clone();
            if disabled {
                self.entries
                    .lock()
                    .unwrap_or_else(|error| error.into_inner())
                    .insert(
                        id,
                        PluginEntry {
                            manifest,
                            manifest_path,
                            active: None,
                            enabled: false,
                            detail: Some("disabled by marker".to_owned()),
                        },
                    );
                continue;
            }
            match self.start_instance(manifest.clone(), manifest_path.clone(), &[]) {
                Ok(instance) => {
                    self.entries
                        .lock()
                        .unwrap_or_else(|error| error.into_inner())
                        .insert(
                            id,
                            PluginEntry {
                                manifest,
                                manifest_path,
                                active: Some(instance),
                                enabled: true,
                                detail: None,
                            },
                        );
                }
                Err(error) => {
                    self.entries
                        .lock()
                        .unwrap_or_else(|error| error.into_inner())
                        .insert(
                            id,
                            PluginEntry {
                                manifest,
                                manifest_path,
                                active: None,
                                enabled: true,
                                detail: Some(error.to_string()),
                            },
                        );
                }
            }
        }
    }
}

fn manifest_policy(
    manifest: &PluginManifest,
    manifest_path: &Path,
    workspace: &Path,
) -> SandboxPolicy {
    let plugin_dir = PluginManifest::manifest_dir(manifest_path);
    let read_roots = permission_roots(workspace, &manifest.permissions.fs_read);
    let write_roots = permission_roots(workspace, &manifest.permissions.fs_write);
    let mut policy =
        SandboxPolicy::plugin(&manifest.id, workspace, plugin_dir, read_roots, write_roots);
    policy.allow_network = !manifest.permissions.network.is_empty();
    policy
}

fn permission_roots(workspace: &Path, entries: &[String]) -> Vec<PathBuf> {
    entries
        .iter()
        .map(|entry| {
            let entry = entry.trim().trim_matches('/').replace('\\', "/");
            if entry.is_empty() || entry == "." || entry == "workspace" {
                workspace.to_path_buf()
            } else {
                workspace.join(entry)
            }
        })
        .collect()
}

fn plugin_sandbox_error(error: SandboxError) -> PluginError {
    match error {
        SandboxError::Violation(violation) => PluginError::Sandbox(violation),
        other => PluginError::PermissionDenied(other.to_string()),
    }
}
fn current_manifest(plugin_root: &Path) -> Option<PathBuf> {
    let text = fs::read_to_string(plugin_root.join("current.json")).ok()?;
    let value: Value = serde_json::from_str(&text).ok()?;
    let relative = value.get("manifest_path")?.as_str()?;
    let path = plugin_root.join(relative);
    path.exists().then_some(path)
}

pub fn find_manifest(dir: &Path) -> Option<PathBuf> {
    for name in ["manifest.json", "manifest.toml"] {
        let path = dir.join(name);
        if path.exists() {
            return Some(path);
        }
    }
    None
}

fn copy_dir(source: &Path, target: &Path) -> Result<(), PluginError> {
    fs::create_dir_all(target)?;
    for entry in WalkDir::new(source).into_iter().filter_map(Result::ok) {
        let relative = entry.path().strip_prefix(source).unwrap_or(entry.path());
        if relative.as_os_str().is_empty() {
            continue;
        }
        let destination = target.join(relative);
        if entry.file_type().is_dir() {
            fs::create_dir_all(&destination)?;
        } else {
            if let Some(parent) = destination.parent() {
                fs::create_dir_all(parent)?;
            }
            fs::copy(entry.path(), &destination)?;
        }
    }
    Ok(())
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<(), PluginError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temporary = path.with_extension("tmp");
    fs::write(&temporary, bytes)?;
    if path.exists() {
        fs::remove_file(path)?;
    }
    fs::rename(temporary, path)?;
    Ok(())
}

fn restore_file(path: &Path, previous: Option<&[u8]>) -> Result<(), PluginError> {
    match previous {
        Some(bytes) => write_atomic(path, bytes),
        None => {
            if path.exists() {
                fs::remove_file(path)?;
            }
            Ok(())
        }
    }
}

fn validate_tool_permissions(
    manifest: &PluginManifest,
    args: &Value,
    workspace: &Path,
) -> Result<(), PluginError> {
    for host in collect_hosts(args) {
        if !host_allowed(&manifest.permissions.network, &host) {
            return Err(PluginError::PermissionDenied(format!(
                "network host `{host}` is not granted to plugin `{}`",
                manifest.id
            )));
        }
    }

    if let Some(raw_path) = args.get("path").and_then(Value::as_str) {
        let policy = PolicyEngine::new(workspace, PolicyConfig::default())
            .map_err(|error| PluginError::PermissionDenied(error.to_string()))?;
        let resolved = policy
            .resolve(raw_path)
            .map_err(|error| PluginError::PermissionDenied(error.to_string()))?;
        let workspace_root = policy.workspace();
        let relative = resolved
            .strip_prefix(workspace_root)
            .map_err(|_| PluginError::PermissionDenied("path is outside the workspace".to_owned()))?
            .to_string_lossy()
            .replace('\\', "/");
        let write = args.get("write").and_then(Value::as_bool).unwrap_or(false)
            || args.get("mode").and_then(Value::as_str) == Some("write");
        let allowed = if write {
            &manifest.permissions.fs_write
        } else {
            &manifest.permissions.fs_read
        };
        if !path_allowed(allowed, &relative) {
            return Err(PluginError::PermissionDenied(format!(
                "filesystem path `{raw_path}` is not granted to plugin `{}`",
                manifest.id
            )));
        }
    }
    Ok(())
}

fn collect_hosts(args: &Value) -> Vec<String> {
    let mut hosts = Vec::new();
    if let Some(url) = args.get("url").and_then(Value::as_str) {
        if let Some(host) = extract_host(url) {
            hosts.push(host);
        }
    }
    if let Some(host) = args.get("host").and_then(Value::as_str) {
        hosts.push(host.to_lowercase());
    }
    if let Some(items) = args.get("network").and_then(Value::as_array) {
        for item in items {
            if let Some(value) = item.as_str() {
                if let Some(host) = extract_host(value) {
                    hosts.push(host);
                }
            }
        }
    }
    hosts
}

fn extract_host(value: &str) -> Option<String> {
    let rest = value
        .split_once("://")
        .map(|(_, rest)| rest)
        .unwrap_or(value);
    let host = rest
        .split(['/', '?', '#'])
        .next()?
        .split(':')
        .next()?
        .trim()
        .to_lowercase();
    (!host.is_empty()).then_some(host)
}

fn host_allowed(allowed: &[String], host: &str) -> bool {
    allowed.iter().any(|entry| {
        let entry = entry.trim().to_lowercase();
        if let Some(suffix) = entry.strip_prefix("*.") {
            host == suffix || host.ends_with(&format!(".{suffix}"))
        } else if let Some(suffix) = entry.strip_prefix('.') {
            host == suffix || host.ends_with(&format!(".{suffix}"))
        } else {
            host == entry
        }
    })
}

fn path_allowed(allowed: &[String], relative: &str) -> bool {
    allowed.iter().any(|entry| {
        let entry = entry.trim().trim_matches('/').replace('\\', "/");
        entry == "workspace" || relative == entry || relative.starts_with(&format!("{entry}/"))
    })
}
