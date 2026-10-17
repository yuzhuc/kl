//! Sandbox backend probing.
//!
//! KL's default is a policy-only backend. Strict mode requires a real
//! process-isolating backend; if MXC is unavailable, strict mode fails closed
//! instead of silently falling back. Non-strict mode isolates the external MXC
//! Z: enumeration failure by using the policy-only backend.

use kl_exec::CommandSpec;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::time::Duration;

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
pub enum SandboxBackend {
    #[default]
    Auto,
    Mxc,
    Policy,
    Off,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct SandboxConfig {
    pub mode: SandboxMode,
    pub backend: SandboxBackend,
    /// Optional shell command used to probe MXC. `--version` is appended.
    pub mxc_command: Option<String>,
    pub probe_timeout_ms: u64,
}

impl Default for SandboxConfig {
    fn default() -> Self {
        Self {
            mode: SandboxMode::Policy,
            backend: SandboxBackend::Auto,
            mxc_command: None,
            probe_timeout_ms: 1500,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SandboxStatus {
    pub mode: SandboxMode,
    pub backend: String,
    pub available: bool,
    pub strict: bool,
    pub detail: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub mxc_error: Option<String>,
    pub isolated_z_failure: bool,
}

#[derive(Debug, Clone, thiserror::Error)]
pub enum SandboxError {
    #[error("sandbox unavailable: {0}")]
    Unavailable(String),
}

#[derive(Debug, Clone)]
pub struct SandboxManager {
    status: SandboxStatus,
}

impl SandboxManager {
    pub fn probe(config: &SandboxConfig) -> Self {
        let strict = config.mode == SandboxMode::Strict;
        let status = match config.backend {
            SandboxBackend::Off => SandboxStatus {
                mode: config.mode,
                backend: "off".to_owned(),
                available: !strict,
                strict,
                detail: if strict {
                    "strict mode cannot run with the sandbox disabled".to_owned()
                } else {
                    "sandbox disabled by configuration".to_owned()
                },
                mxc_error: None,
                isolated_z_failure: false,
            },
            SandboxBackend::Policy => {
                let available = !strict;
                SandboxStatus {
                    mode: config.mode,
                    backend: "policy".to_owned(),
                    available,
                    strict,
                    detail: if strict {
                        "strict mode requires an OS sandbox backend; policy-only is not sufficient"
                            .to_owned()
                    } else {
                        "policy-only: path and command policy, no OS-level isolation".to_owned()
                    },
                    mxc_error: None,
                    isolated_z_failure: false,
                }
            }
            SandboxBackend::Mxc | SandboxBackend::Auto => {
                let probe = probe_mxc(config);
                if probe.available {
                    SandboxStatus {
                        mode: config.mode,
                        backend: "mxc".to_owned(),
                        available: true,
                        strict,
                        detail: probe.detail,
                        mxc_error: None,
                        isolated_z_failure: false,
                    }
                } else if strict {
                    SandboxStatus {
                        mode: config.mode,
                        backend: "mxc".to_owned(),
                        available: false,
                        strict,
                        detail: format!(
                            "strict mode fails closed: MXC unavailable ({})",
                            probe.detail
                        ),
                        mxc_error: Some(probe.detail),
                        isolated_z_failure: probe.isolated_z_failure,
                    }
                } else {
                    SandboxStatus {
                        mode: config.mode,
                        backend: "policy".to_owned(),
                        available: true,
                        strict,
                        detail: format!(
                            "MXC unavailable ({}); isolated with policy-only fallback",
                            probe.detail
                        ),
                        mxc_error: Some(probe.detail),
                        isolated_z_failure: probe.isolated_z_failure,
                    }
                }
            }
        };
        Self { status }
    }

    /// Test/helper constructor for the default policy-only backend.
    pub fn policy_only() -> Self {
        Self::probe(&SandboxConfig {
            mode: SandboxMode::Policy,
            backend: SandboxBackend::Policy,
            ..SandboxConfig::default()
        })
    }

    pub fn status(&self) -> &SandboxStatus {
        &self.status
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

    // Detect a launcher injected into child processes even when MXC is not
    // configured explicitly. This is how the Z: enumeration failure surfaces.
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
    fn policy_mode_is_available() {
        let manager = SandboxManager::policy_only();
        assert!(manager.status().available);
        assert_eq!(manager.status().backend, "policy");
        assert!(manager.ensure_process_execution().is_ok());
    }

    #[test]
    fn strict_policy_is_fail_closed() {
        let manager = SandboxManager::probe(&SandboxConfig {
            mode: SandboxMode::Strict,
            backend: SandboxBackend::Policy,
            ..SandboxConfig::default()
        });
        assert!(!manager.status().available);
        assert!(manager.status().strict);
        assert!(manager.ensure_process_execution().is_err());
    }

    #[test]
    fn strict_auto_without_mxc_is_fail_closed() {
        let manager = SandboxManager::probe(&SandboxConfig {
            mode: SandboxMode::Strict,
            backend: SandboxBackend::Auto,
            mxc_command: Some("definitely-not-a-real-mxc-command".to_owned()),
            probe_timeout_ms: 500,
        });
        assert!(!manager.status().available);
        assert!(manager.ensure_process_execution().is_err());
    }

    #[test]
    fn z_enumeration_failure_is_classified() {
        assert!(is_z_failure(
            "MXC launcher: enumerate MXC volume Z:\\: network name no longer available"
        ));
        assert!(!is_z_failure("all good"));
    }
}
