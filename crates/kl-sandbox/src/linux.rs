//! bubblewrap backend for Linux.
//!
//! The host is mounted read-only, writable roots are rebound, and the process
//! runs in fresh user/PID/IPC/UTS namespaces. Policies that deny network also
//! get a fresh network namespace with only loopback.

use crate::{
    SandboxBackend, SandboxCapabilities, SandboxChild, SandboxConfig, SandboxError, SandboxPolicy,
    SandboxProbe, SandboxRequest, enforce_policy, spawn_wrapped,
};
use kl_exec::CommandSpec;
use std::ffi::OsString;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::AtomicBool;
use std::time::Duration;

pub(crate) fn bubblewrap_backend() -> Arc<dyn SandboxBackend> {
    Arc::new(BubblewrapBackend)
}

struct BubblewrapBackend;

impl SandboxBackend for BubblewrapBackend {
    fn name(&self) -> &'static str {
        "bubblewrap"
    }

    fn probe(&self, config: &SandboxConfig) -> SandboxProbe {
        let Some(bwrap) = find_bwrap() else {
            return SandboxProbe {
                available: false,
                detail: "bubblewrap not found on PATH; set KL_BWRAP_PATH or install bubblewrap"
                    .to_owned(),
                capabilities: SandboxCapabilities::unimplemented(),
            };
        };
        let timeout = Duration::from_millis(config.probe_timeout_ms.max(100));
        match run_probe(&bwrap, timeout) {
            Ok(()) => SandboxProbe {
                available: true,
                detail: format!("bubblewrap ready at {}", bwrap.display()),
                capabilities: SandboxCapabilities::bubblewrap(true),
            },
            Err(error) => SandboxProbe {
                available: false,
                detail: error,
                capabilities: SandboxCapabilities::unimplemented(),
            },
        }
    }

    fn capabilities(&self) -> SandboxCapabilities {
        SandboxCapabilities::bubblewrap(true)
    }

    fn spawn(
        &self,
        spec: &CommandSpec,
        policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError> {
        let bwrap = find_bwrap().ok_or_else(|| {
            SandboxError::Unavailable(
                "bubblewrap not found on PATH; set KL_BWRAP_PATH or install bubblewrap".to_owned(),
            )
        })?;
        spawn_wrapped(spec, &bwrap, launcher_args(spec, policy))
    }

    fn enforce(
        &self,
        request: &SandboxRequest,
        policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        enforce_policy(request, policy)
    }
}

/// Full bubblewrap argv, including the wrapped program.
pub(crate) fn launcher_args(spec: &CommandSpec, policy: &SandboxPolicy) -> Vec<OsString> {
    let mut args: Vec<OsString> = [
        "--new-session",
        "--die-with-parent",
        "--ro-bind",
        "/",
        "/",
        "--dev",
        "/dev",
        "--tmpfs",
        "/tmp",
        "--unshare-user",
        "--unshare-ipc",
        "--unshare-uts",
        "--unshare-pid",
    ]
    .into_iter()
    .map(OsString::from)
    .collect();
    if !policy.allow_network {
        args.push("--unshare-net".into());
    }
    args.extend([
        "--proc".into(),
        "/proc".into(),
        "--cap-drop".into(),
        "ALL".into(),
    ]);
    for root in &policy.write_roots {
        if root.exists() {
            args.extend([
                "--bind".into(),
                root.clone().into_os_string(),
                root.clone().into_os_string(),
            ]);
        }
    }
    for path in &policy.deny_paths {
        let Ok(metadata) = std::fs::symlink_metadata(path) else {
            continue;
        };
        if metadata.is_dir() {
            args.extend([
                "--tmpfs".into(),
                path.clone().into_os_string(),
                "--remount-ro".into(),
                path.clone().into_os_string(),
            ]);
        } else {
            args.extend([
                "--ro-bind".into(),
                "/dev/null".into(),
                path.clone().into_os_string(),
            ]);
        }
    }
    args.push("--chdir".into());
    args.push(spec.cwd.clone().into_os_string());
    args.push("--".into());
    args.push(spec.program.clone().into());
    args.extend(spec.args.iter().map(OsString::from));
    args
}

pub(crate) fn find_bwrap() -> Option<PathBuf> {
    if let Some(path) = std::env::var_os("KL_BWRAP_PATH") {
        let path = PathBuf::from(path);
        if is_executable(&path) {
            return Some(path);
        }
    }
    let search = std::env::var_os("PATH")?;
    std::env::split_paths(&search)
        .map(|directory| directory.join("bwrap"))
        .find(|path| is_executable(path))
}

fn is_executable(path: &Path) -> bool {
    path.metadata()
        .is_ok_and(|metadata| metadata.is_file() && metadata.permissions().mode() & 0o111 != 0)
}

fn run_probe(bwrap: &Path, timeout: Duration) -> Result<(), String> {
    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("/"));
    let mut spec = CommandSpec::new(
        bwrap.to_string_lossy().to_string(),
        [
            "--unshare-user",
            "--unshare-net",
            "--ro-bind",
            "/",
            "/",
            "--dev",
            "/dev",
            "--",
            "/bin/true",
        ]
        .into_iter()
        .map(str::to_owned)
        .collect(),
        cwd,
    );
    spec.timeout = timeout;
    spec.max_output_bytes = 16 * 1024;
    let output = kl_exec::run(&spec, &AtomicBool::new(false)).map_err(|error| error.to_string())?;
    if output.timed_out {
        return Err(format!(
            "bubblewrap probe timed out after {} ms",
            timeout.as_millis()
        ));
    }
    if output.success() {
        return Ok(());
    }
    let detail = output.combined();
    Err(if detail.trim().is_empty() {
        format!("bubblewrap probe exited with {:?}", output.status)
    } else {
        detail.trim().to_owned()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn launcher_args_isolate_network_and_rebind_workspace() {
        let workspace = tempfile::tempdir().unwrap();
        let spec = CommandSpec::new(
            "sh",
            vec!["-c".to_owned(), "true".to_owned()],
            workspace.path(),
        );
        let policy = SandboxPolicy::workspace(workspace.path());
        let args = launcher_args(&spec, &policy);
        let text: Vec<String> = args
            .iter()
            .map(|arg| arg.to_string_lossy().into())
            .collect();
        let joined = text.join("\n");
        assert!(joined.contains("--unshare-net"));
        assert!(joined.contains("--unshare-pid"));
        assert!(joined.contains("--bind"));
        assert!(joined.contains(workspace.path().to_string_lossy().as_ref()));
        assert_eq!(text.last().unwrap(), "true");
    }

    #[test]
    fn launcher_args_keep_network_when_policy_allows_it() {
        let workspace = tempfile::tempdir().unwrap();
        let spec = CommandSpec::new("sh", vec![], workspace.path());
        let mut policy = SandboxPolicy::workspace(workspace.path());
        policy.allow_network = true;
        let args = launcher_args(&spec, &policy);
        let text: Vec<String> = args
            .iter()
            .map(|arg| arg.to_string_lossy().into())
            .collect();
        assert!(!text.iter().any(|arg| arg == "--unshare-net"));
    }
}
