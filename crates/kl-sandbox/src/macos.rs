//! macOS Seatbelt backend.
//!
//! sandbox-exec applies a deny-by-default profile: platform directories and
//! the granted roots stay readable, only writable roots are writable, and
//! network syscalls are denied unless the policy allows them.

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

const SEATBELT: &str = "/usr/bin/sandbox-exec";

const PLATFORM_READ_ROOTS: &[&str] = &[
    "/System",
    "/usr",
    "/bin",
    "/sbin",
    "/Library",
    "/Applications",
    "/opt",
    "/private/etc",
    "/private/var/db",
    "/private/var/run",
    "/nix",
    "/dev",
];

pub(crate) fn seatbelt_backend() -> Arc<dyn SandboxBackend> {
    Arc::new(SeatbeltBackend)
}

struct SeatbeltBackend;

impl SandboxBackend for SeatbeltBackend {
    fn name(&self) -> &'static str {
        "seatbelt"
    }

    fn probe(&self, config: &SandboxConfig) -> SandboxProbe {
        let executable = Path::new(SEATBELT);
        if !is_executable(executable) {
            return SandboxProbe {
                available: false,
                detail: format!("{SEATBELT} is missing or not executable"),
                capabilities: SandboxCapabilities::unimplemented(),
            };
        }
        let timeout = Duration::from_millis(config.probe_timeout_ms.max(100));
        match run_probe(timeout) {
            Ok(()) => SandboxProbe {
                available: true,
                detail: format!("Seatbelt ready at {SEATBELT}"),
                capabilities: SandboxCapabilities::seatbelt(true),
            },
            Err(error) => SandboxProbe {
                available: false,
                detail: error,
                capabilities: SandboxCapabilities::unimplemented(),
            },
        }
    }

    fn capabilities(&self) -> SandboxCapabilities {
        SandboxCapabilities::seatbelt(true)
    }

    fn spawn(
        &self,
        spec: &CommandSpec,
        policy: &SandboxPolicy,
    ) -> Result<SandboxChild, SandboxError> {
        if !is_executable(Path::new(SEATBELT)) {
            return Err(SandboxError::Unavailable(format!(
                "{SEATBELT} is missing or not executable"
            )));
        }
        spawn_wrapped(spec, SEATBELT, launcher_args(spec, policy))
    }

    fn enforce(
        &self,
        request: &SandboxRequest,
        policy: &SandboxPolicy,
    ) -> Result<(), SandboxError> {
        enforce_policy(request, policy)
    }
}

/// Full sandbox-exec argv, including the wrapped program.
pub(crate) fn launcher_args(spec: &CommandSpec, policy: &SandboxPolicy) -> Vec<OsString> {
    let mut args = vec![OsString::from("-p"), OsString::from(profile(policy))];
    args.push("--".into());
    args.push(spec.program.clone().into());
    args.extend(spec.args.iter().map(OsString::from));
    args
}

pub(crate) fn profile(policy: &SandboxPolicy) -> String {
    let mut rules = String::from(
        r#"(version 1)
(deny default)
(allow process-exec)
(allow process-fork)
(allow signal (target same-sandbox))
(allow process-info* (target same-sandbox))
(allow sysctl-read)
(allow mach-lookup)
(allow ipc-posix-shm-read-data ipc-posix-shm-write-create ipc-posix-shm-write-unlink)
(allow pseudo-tty)
(allow file-ioctl)
(allow file-read-metadata file-test-existence)
(allow file-map-executable
  (subpath "/System")
  (subpath "/usr")
  (subpath "/bin")
  (subpath "/sbin")
  (subpath "/Library")
  (subpath "/opt")
  (subpath "/usr/local"))
(allow file-read* (literal "/"))
(allow file-read* file-test-existence
  (literal "/etc")
  (literal "/tmp")
  (literal "/var")
  (literal "/dev/autofs_nowait"))
(allow iokit-open (iokit-registry-entry-class "RootDomainUserClient"))
(allow system-mac-syscall (mac-policy-name "vnguard"))
"#,
    );
    for root in PLATFORM_READ_ROOTS {
        if Path::new(root).exists() {
            rules.push_str(&format!("(allow file-read* (subpath {}))\n", quote(root)));
        }
    }
    let mut read_roots: Vec<PathBuf> = policy.read_roots.clone();
    read_roots.extend(policy.write_roots.iter().cloned());
    read_roots.sort();
    read_roots.dedup();
    for root in &read_roots {
        if let Some(root) = normalized(root) {
            rules.push_str(&format!(
                "(allow file-read* (subpath {}))\n",
                quote_path(&root)
            ));
        }
    }
    rules.push_str(
        "(allow file-read* file-write* (literal \"/dev/null\") (literal \"/dev/zero\") \
         (literal \"/dev/random\") (literal \"/dev/urandom\") (literal \"/dev/tty\") \
         (literal \"/dev/ptmx\"))\n",
    );
    for root in &policy.write_roots {
        if let Some(root) = normalized(root) {
            rules.push_str(&format!(
                "(allow file-write* (subpath {}))\n",
                quote_path(&root)
            ));
        }
    }
    for root in [std::env::temp_dir(), PathBuf::from("/private/tmp")] {
        if let Some(root) = normalized(&root) {
            rules.push_str(&format!(
                "(allow file-read* file-write* (subpath {}))\n",
                quote_path(&root)
            ));
        }
    }
    if policy.allow_network {
        rules.push_str("(allow network-outbound) (allow network-inbound) (allow system-socket)\n");
    } else {
        rules.push_str("(deny network*)\n");
    }
    for path in &policy.deny_paths {
        if let Some(path) = normalized(path) {
            rules.push_str(&format!(
                "(deny file-read* file-write* (subpath {}))\n",
                quote_path(&path)
            ));
        }
    }
    rules
}

/// Seatbelt matches resolved vnode paths, so symlinked roots must be
/// canonicalized before they appear in a subpath rule.
fn normalized(path: &Path) -> Option<PathBuf> {
    std::fs::canonicalize(path).ok()
}

fn quote_path(path: &Path) -> String {
    quote(path.to_string_lossy().as_ref())
}

fn quote(value: &str) -> String {
    let mut quoted = String::with_capacity(value.len() + 2);
    quoted.push('"');
    for character in value.chars() {
        if matches!(character, '"' | '\\') {
            quoted.push('\\');
        }
        quoted.push(character);
    }
    quoted.push('"');
    quoted
}

fn is_executable(path: &Path) -> bool {
    path.metadata()
        .is_ok_and(|metadata| metadata.is_file() && metadata.permissions().mode() & 0o111 != 0)
}

fn run_probe(timeout: Duration) -> Result<(), String> {
    let cwd = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("/"));
    let policy = SandboxPolicy::workspace(cwd.clone());
    let mut spec = CommandSpec::new(
        SEATBELT,
        vec![
            "-p".to_owned(),
            profile(&policy),
            "--".to_owned(),
            "/usr/bin/true".to_owned(),
        ],
        cwd,
    );
    spec.timeout = timeout;
    spec.max_output_bytes = 16 * 1024;
    let output = kl_exec::run(&spec, &AtomicBool::new(false)).map_err(|error| error.to_string())?;
    if output.timed_out {
        return Err(format!(
            "Seatbelt probe timed out after {} ms",
            timeout.as_millis()
        ));
    }
    if output.success() {
        return Ok(());
    }
    let detail = output.combined();
    Err(if detail.trim().is_empty() {
        format!("Seatbelt probe exited with {:?}", output.status)
    } else {
        detail.trim().to_owned()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn profile_denies_network_and_limits_writes() {
        let workspace = tempfile::tempdir().unwrap();
        let policy = SandboxPolicy::workspace(workspace.path());
        let text = profile(&policy);
        assert!(text.starts_with("(version 1)\n(deny default)"));
        assert!(text.contains("(deny network*)"));
        let root = normalized(workspace.path()).unwrap();
        assert!(text.contains(&format!(
            "(allow file-write* (subpath {}))",
            quote_path(&root)
        )));
        assert!(text.contains("/usr"));
    }

    #[test]
    fn profile_allows_network_when_policy_does() {
        let workspace = tempfile::tempdir().unwrap();
        let mut policy = SandboxPolicy::workspace(workspace.path());
        policy.allow_network = true;
        let text = profile(&policy);
        assert!(text.contains("(allow network*)"));
        assert!(!text.contains("(deny network*)"));
    }

    #[test]
    fn paths_with_quotes_are_escaped() {
        assert_eq!(quote("/tmp/a\"b\\c"), "\"/tmp/a\\\"b\\\\c\"");
    }
}
