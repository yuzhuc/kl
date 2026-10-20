//! Unified process sandbox boundary.
//!
//! The policy-only backend remains available for non-strict operation, but it
//! is not an OS sandbox. Strict mode selects a real OS backend: AppContainer on
//! Windows, bubblewrap on Linux, and Seatbelt on macOS.

#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "macos")]
mod macos;
#[cfg(windows)]
mod windows;

use kl_exec::{CommandOutput, CommandSpec};
use serde::{Deserialize, Serialize};
use std::ffi::OsString;
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SandboxMode {
    Off,
    #[default]
    Policy,
    Strict,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SandboxBackendKind {
    #[default]
    Auto,
    #[serde(rename = "appcontainer")]
    AppContainer,
    #[serde(rename = "bubblewrap")]
    Bubblewrap,
    #[serde(rename = "seatbelt")]
    Seatbelt,
    Mxc,
    Policy,
    Off,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct SandboxConfig {
    pub mode: SandboxMode,
    pub backend: SandboxBackendKind,
    /// Optional command used to probe the external MXC launcher.
    pub mxc_command: Option<String>,
    pub probe_timeout_ms: u64,
}

impl Default for SandboxConfig {
    fn default() -> Self {
        Self {
            mode: SandboxMode::Policy,
            backend: SandboxBackendKind::Auto,
            mxc_command: None,
            probe_timeout_ms: 1500,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SandboxCapabilities {
    pub os_isolation: bool,
    pub filesystem_read_restriction: bool,
    pub filesystem_write_restriction: bool,
    pub child_process_containment: bool,
    pub network_isolation: bool,
    pub notes: Vec<String>,
}

impl SandboxCapabilities {
    pub fn policy_only() -> Self {
        Self {
            os_isolation: false,
            filesystem_read_restriction: false,
            filesystem_write_restriction: false,
            child_process_containment: false,
            network_isolation: false,
            notes: vec![
                "policy-only: path and command checks, no OS-level file isolation".to_owned(),
            ],
        }
    }

    pub fn app_container() -> Self {
        Self {
            os_isolation: true,
            filesystem_read_restriction: true,
            filesystem_write_restriction: true,
            child_process_containment: true,
            network_isolation: false,
            notes: vec![
                "Windows AppContainer token restricts filesystem access to granted roots"
                    .to_owned(),
                "network isolation is not verified; policy allowlist remains active".to_owned(),
            ],
        }
    }

    pub fn bubblewrap(network_isolation: bool) -> Self {
        Self {
            os_isolation: true,
            filesystem_read_restriction: false,
            filesystem_write_restriction: true,
            child_process_containment: true,
            network_isolation,
            notes: vec![
                "bubblewrap mounts the host read-only and rebinds writable roots".to_owned(),
                "platform paths remain readable; the policy layer still gates file tools"
                    .to_owned(),
            ],
        }
    }

    pub fn seatbelt(network_isolation: bool) -> Self {
        Self {
            os_isolation: true,
            filesystem_read_restriction: true,
            filesystem_write_restriction: true,
            child_process_containment: false,
            network_isolation,
            notes: vec![
                "macOS Seatbelt profile allows platform reads and the granted roots".to_owned(),
                "child processes inherit the profile but no separate PID namespace is used"
                    .to_owned(),
            ],
        }
    }

    pub fn unimplemented() -> Self {
        Self {
            os_isolation: false,
            filesystem_read_restriction: false,
            filesystem_write_restriction: false,
            child_process_containment: false,
            network_isolation: false,
            notes: vec!["OS sandbox backend is not implemented on this platform".to_owned()],
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SandboxProbe {
    pub available: bool,
    pub detail: String,
    pub capabilities: SandboxCapabilities,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SandboxStatus {
    pub mode: SandboxMode,
    pub backend: String,
    pub available: bool,
    pub strict: bool,
    pub detail: String,
    pub probe_detail: String,
    pub capabilities: SandboxCapabilities,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mxc_error: Option<String>,
    pub isolated_z_failure: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SandboxPolicy {
    pub label: String,
    pub workspace: PathBuf,
    pub read_roots: Vec<PathBuf>,
    pub write_roots: Vec<PathBuf>,
    pub deny_paths: Vec<PathBuf>,
    pub allow_network: bool,
}

impl SandboxPolicy {
    pub fn workspace(workspace: impl Into<PathBuf>) -> Self {
        let workspace = workspace.into();
        Self {
            label: format!("workspace-{}", stable_path_hash(&workspace)),
            read_roots: vec![workspace.clone()],
            write_roots: vec![workspace.clone()],
            deny_paths: sensitive_paths(&workspace),
            workspace,
            allow_network: false,
        }
    }

    pub fn plugin(
        id: &str,
        workspace: impl Into<PathBuf>,
        plugin_dir: impl Into<PathBuf>,
        read_roots: Vec<PathBuf>,
        write_roots: Vec<PathBuf>,
    ) -> Self {
        let workspace = workspace.into();
        let mut read_roots = read_roots;
        read_roots.push(plugin_dir.into());
        Self {
            label: format!("plugin-{id}-{}", stable_path_hash(&workspace)),
            deny_paths: sensitive_paths(&workspace),
            workspace,
            read_roots,
            write_roots,
            allow_network: false,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SandboxViolationKind {
    WriteOutsideWorkspace,
    ReadOutsideWorkspace,
    ReadSensitivePath,
    ChildProcessEscape,
    NetworkDenied,
    PolicyDenied,
    BackendUnavailable,
}

impl SandboxViolationKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::WriteOutsideWorkspace => "write_outside_workspace",
            Self::ReadOutsideWorkspace => "read_outside_workspace",
            Self::ReadSensitivePath => "read_sensitive_path",
            Self::ChildProcessEscape => "child_process_escape",
            Self::NetworkDenied => "network_denied",
            Self::PolicyDenied => "policy_denied",
            Self::BackendUnavailable => "backend_unavailable",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SandboxViolation {
    pub kind: SandboxViolationKind,
    pub resource: String,
    pub message: String,
}

impl std::fmt::Display for SandboxViolation {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "{:?} ({}): {}",
            self.kind, self.resource, self.message
        )
    }
}

impl std::error::Error for SandboxViolation {}

#[derive(Debug, Clone)]
pub struct SandboxRequest {
    pub kind: SandboxViolationKind,
    pub resource: String,
}

#[derive(Debug, thiserror::Error)]
pub enum SandboxError {
    #[error("sandbox unavailable: {0}")]
    Unavailable(String),
    #[error("sandbox spawn failed: {0}")]
    Spawn(String),
    #[error("sandbox io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("sandbox violation: {0}")]
    Violation(SandboxViolation),
}

impl SandboxError {
    pub fn violation(
        kind: SandboxViolationKind,
        resource: impl Into<String>,
        message: impl Into<String>,
    ) -> Self {
        let resource = resource.into();
        Self::Violation(SandboxViolation {
            kind,
            message: message.into(),
            resource,
        })
    }

    pub fn is_violation(&self) -> bool {
        matches!(self, Self::Violation(_))
    }
}

pub trait SandboxBackend: Send + Sync {
    fn name(&self) -> &'static str;
    fn probe(&self, config: &SandboxConfig) -> SandboxProbe;
    fn capabilities(&self) -> SandboxCapabilities;
    fn spawn(
        &self,
        spec: &CommandSpec,
        policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError>;
    fn enforce(&self, request: &SandboxRequest, policy: &SandboxPolicy)
    -> Result<(), SandboxError>;
    fn grant(&self, _policy: &SandboxPolicy) -> Result<(), SandboxError> {
        Ok(())
    }
}

#[derive(Clone)]
pub struct SandboxManager {
    status: SandboxStatus,
    backend: Arc<dyn SandboxBackend>,
}

impl SandboxManager {
    pub fn probe(config: &SandboxConfig) -> Self {
        let mxc = probe_mxc(config);
        let strict = config.mode == SandboxMode::Strict;
        let (backend, probe, fallback_detail) = match config.backend {
            SandboxBackendKind::Off => {
                let backend: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "off" });
                let probe = backend.probe(config);
                (backend, probe, None)
            }
            SandboxBackendKind::Policy => {
                let backend: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
                let probe = backend.probe(config);
                (backend, probe, None)
            }
            SandboxBackendKind::Mxc => {
                let backend: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
                let probe = backend.probe(config);
                let detail = if mxc.available {
                    "MXC launcher detected, but it is not a filesystem sandbox; policy-only fallback"
                        .to_owned()
                } else {
                    format!("MXC unavailable ({}); policy-only fallback", mxc.detail)
                };
                (backend, probe, Some(detail))
            }
            SandboxBackendKind::AppContainer => Self::select_app_container(config, strict),
            SandboxBackendKind::Bubblewrap => Self::select_bubblewrap(config, strict),
            SandboxBackendKind::Seatbelt => Self::select_seatbelt(config, strict),
            SandboxBackendKind::Auto => {
                if strict {
                    Self::select_strict_platform(config)
                } else {
                    let backend: Arc<dyn SandboxBackend> =
                        Arc::new(PolicyBackend { name: "policy" });
                    let probe = backend.probe(config);
                    (backend, probe, None)
                }
            }
        };

        let available = probe.available;
        let mut capabilities = probe.capabilities.clone();
        if let Some(detail) = &fallback_detail {
            capabilities.notes.push(detail.clone());
        }
        let detail = if let Some(fallback) = fallback_detail {
            fallback
        } else if strict && !available {
            format!("strict mode fails closed: {}", probe.detail)
        } else {
            probe.detail.clone()
        };
        let status = SandboxStatus {
            mode: config.mode,
            backend: backend.name().to_owned(),
            available,
            strict,
            detail,
            probe_detail: probe.detail,
            capabilities,
            mxc_error: if mxc.available {
                None
            } else {
                Some(mxc.detail)
            },
            isolated_z_failure: mxc.isolated_z_failure,
        };
        Self { status, backend }
    }

    fn select_app_container(
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        #[cfg(windows)]
        {
            let backend = windows::app_container_backend();
            let probe = backend.probe(config);
            if probe.available || strict {
                return (backend, probe, None);
            }
            let fallback: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
            let fallback_probe = fallback.probe(config);
            let detail = format!(
                "appcontainer unavailable ({}); policy-only fallback",
                probe.detail
            );
            (fallback, fallback_probe, Some(detail))
        }
        #[cfg(not(windows))]
        {
            let backend: Arc<dyn SandboxBackend> = Arc::new(UnimplementedBackend {
                name: "appcontainer",
            });
            let probe = backend.probe(config);
            if strict {
                (backend, probe, None)
            } else {
                let fallback: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
                let fallback_probe = fallback.probe(config);
                (
                    fallback,
                    fallback_probe,
                    Some(format!(
                        "appcontainer is not implemented on this platform ({}); policy-only fallback",
                        probe.detail
                    )),
                )
            }
        }
    }
    #[cfg(target_os = "linux")]
    fn select_bubblewrap(
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_os_backend(linux::bubblewrap_backend(), config, strict)
    }

    #[cfg(not(target_os = "linux"))]
    fn select_bubblewrap(
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_unavailable("bubblewrap", config, strict)
    }

    #[cfg(target_os = "macos")]
    fn select_seatbelt(
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_os_backend(macos::seatbelt_backend(), config, strict)
    }

    #[cfg(not(target_os = "macos"))]
    fn select_seatbelt(
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_unavailable("seatbelt", config, strict)
    }

    #[cfg(any(target_os = "linux", target_os = "macos"))]
    fn select_os_backend(
        backend: Arc<dyn SandboxBackend>,
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        let probe = backend.probe(config);
        if probe.available || strict {
            return (backend, probe, None);
        }
        let name = backend.name();
        let fallback: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
        let fallback_probe = fallback.probe(config);
        let detail = format!(
            "{name} unavailable ({}); policy-only fallback",
            probe.detail
        );
        (fallback, fallback_probe, Some(detail))
    }

    fn select_unavailable(
        name: &'static str,
        config: &SandboxConfig,
        strict: bool,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        let backend: Arc<dyn SandboxBackend> = Arc::new(UnimplementedBackend { name });
        let probe = backend.probe(config);
        if strict {
            return (backend, probe, None);
        }
        let fallback: Arc<dyn SandboxBackend> = Arc::new(PolicyBackend { name: "policy" });
        let fallback_probe = fallback.probe(config);
        let detail = format!("{name} is not available on this platform; policy-only fallback");
        (fallback, fallback_probe, Some(detail))
    }

    #[cfg(windows)]
    fn select_strict_platform(
        config: &SandboxConfig,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_app_container(config, true)
    }

    #[cfg(target_os = "linux")]
    fn select_strict_platform(
        config: &SandboxConfig,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_bubblewrap(config, true)
    }

    #[cfg(target_os = "macos")]
    fn select_strict_platform(
        config: &SandboxConfig,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_seatbelt(config, true)
    }

    #[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
    fn select_strict_platform(
        config: &SandboxConfig,
    ) -> (Arc<dyn SandboxBackend>, SandboxProbe, Option<String>) {
        Self::select_unavailable("os-sandbox", config, true)
    }

    pub fn policy_only() -> Self {
        Self::probe(&SandboxConfig {
            mode: SandboxMode::Policy,
            backend: SandboxBackendKind::Policy,
            ..SandboxConfig::default()
        })
    }

    pub fn status(&self) -> &SandboxStatus {
        &self.status
    }

    pub fn backend(&self) -> &dyn SandboxBackend {
        self.backend.as_ref()
    }

    pub fn is_strict(&self) -> bool {
        self.status.strict
    }

    pub fn ensure_process_execution(&self) -> Result<(), SandboxError> {
        if self.status.available {
            Ok(())
        } else {
            Err(SandboxError::Unavailable(self.status.detail.clone()))
        }
    }

    pub fn spawn(
        &self,
        spec: &CommandSpec,
        policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError> {
        self.ensure_process_execution()?;
        self.backend.spawn(spec, policy)
    }

    pub fn run(
        &self,
        spec: &CommandSpec,
        cancel: &AtomicBool,
        policy: &SandboxPolicy,
    ) -> Result<CommandOutput, SandboxError> {
        self.ensure_process_execution()?;
        let mut child = self.spawn(spec, policy)?;
        drop(child.stdin.take());
        let stdout = child.stdout.take();
        let stderr = child.stderr.take();
        let limit = spec.max_output_bytes;
        let stdout_handle = stdout.map(|stream| thread::spawn(move || read_limited(stream, limit)));
        let stderr_handle = stderr.map(|stream| thread::spawn(move || read_limited(stream, limit)));

        let started = Instant::now();
        let mut timed_out = false;
        let mut cancelled = false;
        let status = loop {
            match child.try_wait()? {
                Some(code) => break Some(code),
                None if cancel.load(Ordering::Relaxed) => {
                    cancelled = true;
                    let _ = child.kill();
                    break child.wait().ok();
                }
                None if started.elapsed() >= spec.timeout => {
                    timed_out = true;
                    let _ = child.kill();
                    break child.wait().ok();
                }
                None => thread::sleep(Duration::from_millis(20)),
            }
        };
        let _ = child.cleanup_descendants();
        let (stdout, stdout_truncated) = stdout_handle
            .and_then(|handle| handle.join().ok())
            .unwrap_or_default();
        let (stderr, stderr_truncated) = stderr_handle
            .and_then(|handle| handle.join().ok())
            .unwrap_or_default();
        Ok(CommandOutput {
            status,
            stdout,
            stderr,
            timed_out,
            cancelled,
            truncated: stdout_truncated || stderr_truncated,
            duration_ms: started.elapsed().as_millis() as u64,
        })
    }

    pub fn enforce(
        &self,
        request: &SandboxRequest,
        policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        self.ensure_process_execution()?;
        self.backend.enforce(request, policy)
    }

    pub fn grant(&self, policy: &SandboxPolicy) -> Result<(), SandboxError> {
        self.ensure_process_execution()?;
        self.backend.grant(policy)
    }
}

pub struct SandboxChild {
    inner: ChildInner,
    pub stdin: Option<Box<dyn Write + Send>>,
    pub stdout: Option<Box<dyn Read + Send>>,
    pub stderr: Option<Box<dyn Read + Send>>,
}

enum ChildInner {
    #[cfg(windows)]
    AppContainer(windows::AppContainerChild),
    Process(Child),
}

impl SandboxChild {
    fn from_process(mut child: Child) -> Self {
        let stdin = child
            .stdin
            .take()
            .map(|stream| Box::new(stream) as Box<dyn Write + Send>);
        let stdout = child
            .stdout
            .take()
            .map(|stream| Box::new(stream) as Box<dyn Read + Send>);
        let stderr = child
            .stderr
            .take()
            .map(|stream| Box::new(stream) as Box<dyn Read + Send>);
        Self {
            inner: ChildInner::Process(child),
            stdin,
            stdout,
            stderr,
        }
    }

    #[cfg(windows)]
    fn from_app_container(mut child: windows::AppContainerChild) -> Self {
        Self {
            stdin: child.stdin.take(),
            stdout: child.stdout.take(),
            stderr: child.stderr.take(),
            inner: ChildInner::AppContainer(child),
        }
    }

    pub fn id(&self) -> u32 {
        match &self.inner {
            #[cfg(windows)]
            ChildInner::AppContainer(child) => child.id(),
            ChildInner::Process(child) => child.id(),
        }
    }

    pub fn try_wait(&mut self) -> Result<Option<i32>, SandboxError> {
        match &mut self.inner {
            #[cfg(windows)]
            ChildInner::AppContainer(child) => child.try_wait(),
            ChildInner::Process(child) => {
                Ok(child.try_wait().map_err(SandboxError::Io)?.map(exit_code))
            }
        }
    }

    pub fn wait(&mut self) -> Result<i32, SandboxError> {
        match &mut self.inner {
            #[cfg(windows)]
            ChildInner::AppContainer(child) => child.wait(),
            ChildInner::Process(child) => Ok(exit_code(child.wait().map_err(SandboxError::Io)?)),
        }
    }

    pub fn kill(&mut self) -> Result<(), SandboxError> {
        match &mut self.inner {
            #[cfg(windows)]
            ChildInner::AppContainer(child) => child.kill(),
            ChildInner::Process(child) => {
                #[cfg(windows)]
                {
                    let pid = child.id();
                    let _ = Command::new("taskkill")
                        .args(["/PID", &pid.to_string(), "/T", "/F"])
                        .stdin(Stdio::null())
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .status();
                }
                let _ = child.kill();
                Ok(())
            }
        }
    }

    fn cleanup_descendants(&mut self) -> Result<(), SandboxError> {
        match &mut self.inner {
            #[cfg(windows)]
            ChildInner::AppContainer(child) => child.cleanup_descendants(),
            ChildInner::Process(_) => Ok(()),
        }
    }
}

/// Maps a signal-terminated child to the conventional 128 + signal code so
/// callers and tests can tell a crash from a normal non-zero exit.
fn exit_code(status: std::process::ExitStatus) -> i32 {
    if let Some(code) = status.code() {
        return code;
    }
    #[cfg(unix)]
    {
        use std::os::unix::process::ExitStatusExt;
        128 + status.signal().unwrap_or(0)
    }
    #[cfg(not(unix))]
    {
        -1
    }
}

impl Drop for SandboxChild {
    fn drop(&mut self) {
        let _ = self.kill();
    }
}

/// Starts a launcher with the request environment and child stdio.
///
/// OS backends wrap the requested command (bwrap, sandbox-exec) and pass the
/// original argv through; stdio and env stay identical to direct execution.
pub(crate) fn spawn_wrapped<S, I, A>(
    spec: &CommandSpec,
    program: S,
    args: I,
) -> Result<SandboxChild, SandboxError>
where
    S: AsRef<std::ffi::OsStr>,
    I: IntoIterator<Item = A>,
    A: AsRef<std::ffi::OsStr>,
{
    let mut command = Command::new(program);
    command
        .args(args)
        .current_dir(&spec.cwd)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if spec.clear_env {
        command.env_clear();
    }
    for (name, value) in &spec.env {
        command.env(name, value);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }
    let child = command.spawn().map_err(|error| {
        SandboxError::Spawn(format!("failed to start {}: {error}", spec.program))
    })?;
    Ok(SandboxChild::from_process(child))
}

struct PolicyBackend {
    name: &'static str,
}

impl SandboxBackend for PolicyBackend {
    fn name(&self) -> &'static str {
        self.name
    }

    fn probe(&self, config: &SandboxConfig) -> SandboxProbe {
        let available = config.mode != SandboxMode::Strict;
        SandboxProbe {
            available,
            detail: if config.mode == SandboxMode::Strict {
                "strict mode requires an OS sandbox backend; policy-only is not sufficient"
                    .to_owned()
            } else if self.name == "off" {
                "sandbox disabled by configuration".to_owned()
            } else {
                "policy-only: path and command policy, no OS-level isolation".to_owned()
            },
            capabilities: SandboxCapabilities::policy_only(),
        }
    }

    fn capabilities(&self) -> SandboxCapabilities {
        SandboxCapabilities::policy_only()
    }

    fn spawn(
        &self,
        spec: &CommandSpec,
        _policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError> {
        spawn_wrapped(spec, &spec.program, &spec.args)
    }

    fn enforce(
        &self,
        request: &SandboxRequest,
        policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        enforce_policy(request, policy)
    }
}

struct UnimplementedBackend {
    name: &'static str,
}

impl SandboxBackend for UnimplementedBackend {
    fn name(&self) -> &'static str {
        self.name
    }

    fn probe(&self, _config: &SandboxConfig) -> SandboxProbe {
        SandboxProbe {
            available: false,
            detail: format!(
                "{} backend is not implemented on this platform; strict mode fails closed",
                self.name
            ),
            capabilities: SandboxCapabilities::unimplemented(),
        }
    }

    fn capabilities(&self) -> SandboxCapabilities {
        SandboxCapabilities::unimplemented()
    }

    fn spawn(
        &self,
        _spec: &CommandSpec,
        _policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError> {
        Err(SandboxError::Unavailable(format!(
            "{} backend is not implemented on this platform",
            self.name
        )))
    }

    fn enforce(
        &self,
        _request: &SandboxRequest,
        _policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        Err(SandboxError::Unavailable(format!(
            "{} backend is not implemented on this platform",
            self.name
        )))
    }
}

pub(crate) fn enforce_policy(
    request: &SandboxRequest,
    policy: &SandboxPolicy,
) -> Result<(), SandboxError> {
    if request.kind == SandboxViolationKind::NetworkDenied {
        if policy.allow_network {
            return Ok(());
        }
        return Err(SandboxError::violation(
            SandboxViolationKind::NetworkDenied,
            &request.resource,
            "network access is not allowed by the active sandbox policy",
        ));
    }
    if request.kind == SandboxViolationKind::BackendUnavailable {
        return Err(SandboxError::Unavailable(request.resource.clone()));
    }

    let resolved = resolve_policy_path(&policy.workspace, &request.resource)?;
    let denied = is_sensitive_path(&resolved)
        || policy
            .deny_paths
            .iter()
            .any(|path| path_inside(path, &resolved));
    let readable = policy
        .read_roots
        .iter()
        .chain(policy.write_roots.iter())
        .any(|root| path_inside(root, &resolved));
    let writable = policy
        .write_roots
        .iter()
        .any(|root| path_inside(root, &resolved));

    match request.kind {
        SandboxViolationKind::WriteOutsideWorkspace => {
            if !writable {
                return Err(SandboxError::violation(
                    SandboxViolationKind::WriteOutsideWorkspace,
                    &request.resource,
                    format!(
                        "write path is outside the granted roots: {}",
                        resolved.display()
                    ),
                ));
            }
            if denied {
                return Err(SandboxError::violation(
                    SandboxViolationKind::PolicyDenied,
                    &request.resource,
                    format!("write path is sensitive or denied: {}", resolved.display()),
                ));
            }
        }
        SandboxViolationKind::ReadOutsideWorkspace
        | SandboxViolationKind::ReadSensitivePath
        | SandboxViolationKind::ChildProcessEscape => {
            if !readable {
                return Err(SandboxError::violation(
                    SandboxViolationKind::ReadOutsideWorkspace,
                    &request.resource,
                    format!(
                        "read path is outside the granted roots: {}",
                        resolved.display()
                    ),
                ));
            }
            if denied {
                return Err(SandboxError::violation(
                    SandboxViolationKind::ReadSensitivePath,
                    &request.resource,
                    format!("sensitive path is denied: {}", resolved.display()),
                ));
            }
        }
        SandboxViolationKind::PolicyDenied => {
            return Err(SandboxError::violation(
                SandboxViolationKind::PolicyDenied,
                &request.resource,
                "operation denied by sandbox policy",
            ));
        }
        SandboxViolationKind::NetworkDenied | SandboxViolationKind::BackendUnavailable => {}
    }
    Ok(())
}

fn resolve_policy_path(workspace: &Path, raw: &str) -> Result<PathBuf, SandboxError> {
    if raw.trim().is_empty() {
        return Err(SandboxError::violation(
            SandboxViolationKind::PolicyDenied,
            raw,
            "empty path",
        ));
    }
    let requested = Path::new(raw);
    let candidate = if requested.is_absolute() {
        requested.to_path_buf()
    } else {
        workspace.join(requested)
    };
    let normalized = normalize_components(&candidate);
    let mut existing = normalized.clone();
    let mut tail: Vec<OsString> = Vec::new();
    while !existing.exists() {
        let Some(name) = existing.file_name() else {
            break;
        };
        tail.push(name.to_os_string());
        if !existing.pop() {
            break;
        }
    }
    let mut resolved = std::fs::canonicalize(&existing).unwrap_or(existing);
    for name in tail.into_iter().rev() {
        resolved.push(name);
    }
    Ok(resolved)
}

fn normalize_components(path: &Path) -> PathBuf {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::Prefix(prefix) => normalized.push(prefix.as_os_str()),
            Component::RootDir => normalized.push(Path::new(std::path::MAIN_SEPARATOR_STR)),
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            Component::Normal(part) => normalized.push(part),
        }
    }
    normalized
}

fn path_key(path: &Path) -> String {
    let path = std::fs::canonicalize(path).unwrap_or_else(|_| path.to_path_buf());
    let text = path.to_string_lossy().replace('/', "\\");
    let text = text.strip_prefix("\\\\?\\").unwrap_or(&text);
    text.trim_end_matches('\\').to_ascii_lowercase()
}

fn path_inside(root: &Path, path: &Path) -> bool {
    let root = path_key(root);
    let path = path_key(path);
    path == root || path.starts_with(&(root + "\\"))
}

fn is_sensitive_path(path: &Path) -> bool {
    path.components().any(|component| {
        let Component::Normal(name) = component else {
            return false;
        };
        let name = name.to_string_lossy().to_ascii_lowercase();
        name == ".env"
            || name.starts_with(".env.")
            || matches!(
                name.as_str(),
                ".ssh" | ".aws" | ".gnupg" | ".git" | "id_rsa" | "id_ed25519" | "credentials"
            )
            || name.ends_with(".key")
    })
}
fn read_limited(mut reader: Box<dyn Read + Send>, limit: usize) -> (String, bool) {
    let mut buffer = Vec::new();
    let mut chunk = [0u8; 8192];
    let mut truncated = false;
    loop {
        match reader.read(&mut chunk) {
            Ok(0) => break,
            Ok(count) => {
                let remaining = limit.saturating_sub(buffer.len());
                if count > remaining {
                    buffer.extend_from_slice(&chunk[..remaining]);
                    truncated = true;
                } else {
                    buffer.extend_from_slice(&chunk[..count]);
                }
            }
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(_) => break,
        }
    }
    (String::from_utf8_lossy(&buffer).into_owned(), truncated)
}

fn stable_path_hash(path: &Path) -> u64 {
    let mut hash = 0xcbf29ce484222325u64;
    for byte in path.to_string_lossy().as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x100000001b3);
    }
    hash
}

fn sensitive_paths(workspace: &Path) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    for name in [
        ".env",
        ".ssh",
        ".aws",
        ".gnupg",
        "id_rsa",
        "id_ed25519",
        "credentials",
    ] {
        let path = workspace.join(name);
        if path.exists() {
            paths.push(path);
        }
    }
    if let Ok(entries) = std::fs::read_dir(workspace) {
        for entry in entries.flatten() {
            let name = entry.file_name().to_string_lossy().to_string();
            if name.starts_with(".env") || name.ends_with(".key") {
                paths.push(entry.path());
            }
        }
    }
    paths
}

#[derive(Debug)]
struct MxcProbe {
    available: bool,
    detail: String,
    isolated_z_failure: bool,
}

fn probe_mxc(config: &SandboxConfig) -> MxcProbe {
    let timeout = Duration::from_millis(config.probe_timeout_ms.max(100));
    let command = config
        .mxc_command
        .clone()
        .or_else(|| std::env::var("KL_MXC_PATH").ok())
        .filter(|command| !command.trim().is_empty());

    if let Some(command) = command {
        let command = format!("{command} --version");
        return run_shell_probe(&command, timeout);
    }

    let (program, args) = if cfg!(windows) {
        ("cmd", vec!["/C".to_owned(), "exit 0".to_owned()])
    } else {
        ("sh", vec!["-c".to_owned(), "true".to_owned()])
    };
    match run_probe(program, args, timeout) {
        Ok((status, stdout, stderr)) => {
            let text = format!("{stdout}\n{stderr}");
            if is_z_failure(&text) {
                MxcProbe {
                    available: false,
                    detail:
                        "MXC launcher Z: enumeration failure detected; strict mode will fail closed"
                            .to_owned(),
                    isolated_z_failure: true,
                }
            } else if status == Some(0) {
                MxcProbe {
                    available: false,
                    detail: "MXC backend is not configured; policy-only backend is available"
                        .to_owned(),
                    isolated_z_failure: false,
                }
            } else {
                MxcProbe {
                    available: false,
                    detail: format!("probe command failed with status {status:?}: {text}"),
                    isolated_z_failure: is_z_failure(&text),
                }
            }
        }
        Err(error) => MxcProbe {
            available: false,
            detail: error,
            isolated_z_failure: false,
        },
    }
}

fn run_shell_probe(command: &str, timeout: Duration) -> MxcProbe {
    let (program, args) = if cfg!(windows) {
        ("cmd", vec!["/C".to_owned(), command.to_owned()])
    } else {
        ("sh", vec!["-c".to_owned(), command.to_owned()])
    };
    match run_probe(program, args, timeout) {
        Ok((status, stdout, stderr)) => {
            let text = format!("{stdout}\n{stderr}");
            if is_z_failure(&text) {
                MxcProbe {
                    available: false,
                    detail: "MXC Z: enumeration failure detected".to_owned(),
                    isolated_z_failure: true,
                }
            } else if status == Some(0) {
                MxcProbe {
                    available: true,
                    detail: stdout.trim().to_owned(),
                    isolated_z_failure: false,
                }
            } else {
                MxcProbe {
                    available: false,
                    detail: text.trim().to_owned(),
                    isolated_z_failure: false,
                }
            }
        }
        Err(error) => MxcProbe {
            available: false,
            detail: error,
            isolated_z_failure: false,
        },
    }
}

fn run_probe(
    program: &str,
    args: Vec<String>,
    timeout: Duration,
) -> Result<(Option<i32>, String, String), String> {
    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
    let mut spec = CommandSpec::new(program, args, cwd);
    spec.timeout = timeout;
    spec.max_output_bytes = 16 * 1024;
    let output = kl_exec::run(&spec, &AtomicBool::new(false)).map_err(|error| error.to_string())?;
    if output.timed_out {
        return Err(format!("probe timed out after {} ms", timeout.as_millis()));
    }
    Ok((output.status, output.stdout, output.stderr))
}

pub fn is_z_failure(text: &str) -> bool {
    let lower = text.to_lowercase();
    lower.contains("mxc launcher")
        || lower.contains("enumerate mxc volume")
        || (lower.contains("mxc") && lower.contains("z:"))
}

#[cfg(test)]
mod tests {
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
}
