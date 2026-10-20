//! Pseudo-terminal execution for the shell tool.
//!
//! The command line comes from the sandbox backend, so a tty run keeps the
//! same OS isolation as a normal run. A pty merges stdout and stderr into a
//! single stream, so the captured text is reported as stdout.

use crate::ToolError;
use kl_exec::{CommandOutput, CommandSpec};
use kl_sandbox::SandboxLauncher;
use portable_pty::{Child, CommandBuilder, PtySize, native_pty_system};
use std::io::{Read, Write};
#[cfg(windows)]
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

const ROWS: u16 = 24;
const COLS: u16 = 80;
const READ_CHUNK: usize = 8192;
/// Grace period for the reader thread to drain the pty after the child exits.
const DRAIN_TIMEOUT: Duration = Duration::from_secs(5);

/// Runs `launcher` inside a pseudo-terminal.
pub(crate) fn run(
    spec: &CommandSpec,
    launcher: &SandboxLauncher,
    cancel: &AtomicBool,
) -> Result<CommandOutput, ToolError> {
    let pair = native_pty_system()
        .openpty(PtySize {
            rows: ROWS,
            cols: COLS,
            pixel_width: 0,
            pixel_height: 0,
        })
        .map_err(|error| ToolError::Execution(format!("cannot open pty: {error}")))?;

    let mut command = CommandBuilder::new(launcher.program.as_os_str());
    command.args(launcher.args.iter().map(|arg| arg.as_os_str()));
    command.cwd(launcher.cwd.as_os_str());
    if launcher.clear_env {
        command.env_clear();
    }
    for (key, value) in &launcher.env {
        command.env(key, value);
    }

    let mut child = pair
        .slave
        .spawn_command(command)
        .map_err(|error| ToolError::Execution(format!("cannot start pty command: {error}")))?;
    drop(pair.slave);

    let mut reader = pair
        .master
        .try_clone_reader()
        .map_err(|error| ToolError::Execution(format!("cannot read pty output: {error}")))?;
    // Console programs such as cmd.exe query the terminal with ESC[6n and
    // wait for a cursor-position reply, so the reader doubles as the minimal
    // terminal emulator that answers it.
    let writer = pair
        .master
        .take_writer()
        .map_err(|error| ToolError::Execution(format!("cannot open pty input: {error}")))?;

    let captured = Arc::new(Mutex::new(Captured::default()));
    let sink = Arc::clone(&captured);
    let limit = spec.max_output_bytes;
    let (done_tx, done_rx) = mpsc::channel::<()>();
    let reader_thread = thread::spawn(move || {
        let mut writer = writer;
        let mut chunk = [0u8; READ_CHUNK];
        loop {
            match reader.read(&mut chunk) {
                Ok(0) => break,
                Ok(count) => {
                    if chunk[..count].windows(4).any(|window| window == b"\x1b[6n") {
                        let _ = writer.write_all(b"\x1b[1;1R");
                        let _ = writer.flush();
                    }
                    let mut sink = sink.lock().unwrap_or_else(|error| error.into_inner());
                    let remaining = limit.saturating_sub(sink.bytes.len());
                    if count > remaining {
                        sink.bytes.extend_from_slice(&chunk[..remaining]);
                        sink.truncated = true;
                    } else {
                        sink.bytes.extend_from_slice(&chunk[..count]);
                    }
                }
                Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
                Err(_) => break,
            }
        }
        let _ = done_tx.send(());
    });

    let started = Instant::now();
    let mut timed_out = false;
    let mut cancelled = false;
    let exit = loop {
        match child.try_wait() {
            Ok(Some(exit)) => break Some(exit),
            Ok(None) => {}
            Err(error) => {
                return Err(ToolError::Execution(format!(
                    "cannot wait for pty command: {error}"
                )));
            }
        }
        if cancel.load(Ordering::Relaxed) {
            cancelled = true;
            kill(&mut child);
            break child.wait().ok();
        }
        if started.elapsed() >= spec.timeout {
            timed_out = true;
            kill(&mut child);
            break child.wait().ok();
        }
        thread::sleep(Duration::from_millis(20));
    };

    // Dropping the master closes the console so the reader sees the end of
    // the stream once the command is gone.
    drop(pair.master);
    let drained = done_rx.recv_timeout(DRAIN_TIMEOUT).is_ok();
    if drained {
        let _ = reader_thread.join();
    }

    let (bytes, truncated) = {
        let mut sink = captured.lock().unwrap_or_else(|error| error.into_inner());
        (std::mem::take(&mut sink.bytes), sink.truncated)
    };
    let status = exit.and_then(|exit| {
        if exit.signal().is_some() {
            None
        } else {
            Some(exit.exit_code() as i32)
        }
    });
    Ok(CommandOutput {
        status,
        stdout: String::from_utf8_lossy(&bytes).into_owned(),
        stderr: String::new(),
        timed_out,
        cancelled,
        truncated,
        duration_ms: started.elapsed().as_millis() as u64,
    })
}

#[derive(Default)]
struct Captured {
    bytes: Vec<u8>,
    truncated: bool,
}

fn kill(child: &mut Box<dyn Child + Send + Sync>) {
    #[cfg(windows)]
    if let Some(pid) = child.process_id() {
        let _ = std::process::Command::new("taskkill")
            .args(["/PID", &pid.to_string(), "/T", "/F"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
    let _ = child.kill();
}
