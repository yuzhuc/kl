//! Process execution with cancellation, timeout and bounded output capture.

use std::io::Read;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

#[derive(Debug, Clone)]
pub struct CommandSpec {
    pub program: String,
    pub args: Vec<String>,
    pub cwd: PathBuf,
    pub timeout: Duration,
    pub max_output_bytes: usize,
    pub env: Vec<(String, String)>,
}

impl CommandSpec {
    pub fn new(program: impl Into<String>, args: Vec<String>, cwd: impl Into<PathBuf>) -> Self {
        Self {
            program: program.into(),
            args,
            cwd: cwd.into(),
            timeout: Duration::from_secs(120),
            max_output_bytes: 256 * 1024,
            env: Vec::new(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct CommandOutput {
    pub status: Option<i32>,
    pub stdout: String,
    pub stderr: String,
    pub timed_out: bool,
    pub cancelled: bool,
    pub truncated: bool,
    pub duration_ms: u64,
}

impl CommandOutput {
    pub fn success(&self) -> bool {
        !self.timed_out && !self.cancelled && self.status == Some(0)
    }

    pub fn combined(&self) -> String {
        let mut text = String::new();
        if !self.stdout.is_empty() {
            text.push_str(&self.stdout);
        }
        if !self.stderr.is_empty() {
            if !text.is_empty() && !text.ends_with('\n') {
                text.push('\n');
            }
            text.push_str(&self.stderr);
        }
        text
    }
}

#[derive(Debug, thiserror::Error)]
pub enum ExecError {
    #[error("failed to start {program}: {source}")]
    Spawn {
        program: String,
        #[source]
        source: std::io::Error,
    },
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
}

pub fn run(spec: &CommandSpec, cancel: &AtomicBool) -> Result<CommandOutput, ExecError> {
    let mut command = Command::new(&spec.program);
    command
        .args(&spec.args)
        .current_dir(&spec.cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (key, value) in &spec.env {
        command.env(key, value);
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        command.creation_flags(CREATE_NO_WINDOW);
    }

    let mut child = command.spawn().map_err(|source| ExecError::Spawn {
        program: spec.program.clone(),
        source,
    })?;

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    let limit = spec.max_output_bytes;
    let stdout_handle = stdout.map(|stream| thread::spawn(move || read_limited(stream, limit)));
    let stderr_handle = stderr.map(|stream| thread::spawn(move || read_limited(stream, limit)));

    let started = Instant::now();
    let mut timed_out = false;
    let mut cancelled = false;
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status.code();
        }
        if cancel.load(Ordering::Relaxed) {
            cancelled = true;
            kill_tree(&mut child);
            break child.wait().ok().and_then(|status| status.code());
        }
        if started.elapsed() >= spec.timeout {
            timed_out = true;
            kill_tree(&mut child);
            break child.wait().ok().and_then(|status| status.code());
        }
        thread::sleep(Duration::from_millis(20));
    };

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

fn read_limited(mut reader: impl Read, limit: usize) -> (String, bool) {
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

fn kill_tree(child: &mut std::process::Child) {
    let pid = child.id();
    #[cfg(windows)]
    {
        let _ = Command::new("taskkill")
            .args(["/PID", &pid.to_string(), "/T", "/F"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
    #[cfg(not(windows))]
    {
        let _ = pid;
    }
    let _ = child.kill();
}

#[cfg(test)]
mod tests {
    use super::*;

    fn shell() -> (&'static str, Vec<String>) {
        #[cfg(windows)]
        {
            ("cmd", vec!["/C".to_owned(), "echo hello".to_owned()])
        }
        #[cfg(not(windows))]
        {
            ("sh", vec!["-c".to_owned(), "echo hello".to_owned()])
        }
    }

    #[test]
    fn captures_stdout_and_exit_code() {
        let temp = tempfile::tempdir().unwrap();
        let (program, args) = shell();
        let spec = CommandSpec::new(program, args, temp.path());
        let output = run(&spec, &AtomicBool::new(false)).unwrap();
        assert!(output.success());
        assert!(output.stdout.contains("hello"));
    }

    #[test]
    fn respects_output_limit() {
        let temp = tempfile::tempdir().unwrap();
        let (program, args) = shell();
        let mut spec = CommandSpec::new(program, args, temp.path());
        spec.max_output_bytes = 4;
        let output = run(&spec, &AtomicBool::new(false)).unwrap();
        assert!(output.truncated);
        assert_eq!(output.stdout.len(), 4);
    }

    #[test]
    fn times_out_and_kills() {
        let temp = tempfile::tempdir().unwrap();
        #[cfg(windows)]
        let spec = CommandSpec::new(
            "cmd",
            vec!["/C".to_owned(), "ping 127.0.0.1 -n 5 > nul".to_owned()],
            temp.path(),
        );
        #[cfg(not(windows))]
        let spec = CommandSpec::new(
            "sh",
            vec!["-c".to_owned(), "sleep 5".to_owned()],
            temp.path(),
        );
        let mut spec = spec;
        spec.timeout = Duration::from_millis(100);
        let output = run(&spec, &AtomicBool::new(false)).unwrap();
        assert!(output.timed_out);
    }
}
