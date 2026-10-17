use crate::manifest::PluginManifest;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

#[derive(Debug, thiserror::Error, Clone)]
pub enum SidecarError {
    #[error("cannot start plugin: {0}")]
    Spawn(String),
    #[error("plugin io error: {0}")]
    Io(String),
    #[error("plugin request timed out after {0:?}")]
    Timeout(Duration),
    #[error("plugin exited: {0}")]
    Exited(String),
    #[error("plugin rpc error {code}: {message}")]
    Rpc { code: i64, message: String },
    #[error("plugin protocol error: {0}")]
    Protocol(String),
}

type Pending = Arc<Mutex<HashMap<u64, mpsc::Sender<Result<Value, SidecarError>>>>>;

pub(crate) struct SidecarProcess {
    child: Mutex<Child>,
    stdin: Mutex<Option<ChildStdin>>,
    pending: Pending,
    next_id: AtomicU64,
    request_lock: Mutex<()>,
    alive: Arc<AtomicBool>,
    stderr_log: Arc<Mutex<Vec<String>>>,
    reader_handle: Mutex<Option<JoinHandle<()>>>,
    stderr_handle: Mutex<Option<JoinHandle<()>>>,
}

impl SidecarProcess {
    pub fn spawn(
        manifest: &PluginManifest,
        manifest_path: &std::path::Path,
        token: &str,
        env_overrides: &[(String, String)],
    ) -> Result<Self, SidecarError> {
        let entrypoint = manifest.entrypoint_path(manifest_path);
        let mut command = Command::new(&entrypoint);
        command
            .current_dir(PluginManifest::manifest_dir(manifest_path))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .env_clear();
        add_safe_environment(&mut command);
        for name in &manifest.permissions.env {
            if let Ok(value) = std::env::var(name) {
                command.env(name, value);
            }
        }
        for (name, value) in env_overrides {
            command.env(name, value);
        }
        command
            .env("KL_PLUGIN_ID", &manifest.id)
            .env("KL_PLUGIN_VERSION", &manifest.version)
            .env("KL_PLUGIN_TOKEN", token);
        if let Some(config_path) = manifest.config_path(manifest_path) {
            if config_path.exists() {
                command.env("KL_PLUGIN_CONFIG", config_path);
            }
        }
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            command.creation_flags(CREATE_NO_WINDOW);
        }

        let mut child = command
            .spawn()
            .map_err(|error| SidecarError::Spawn(format!("{}: {error}", entrypoint.display())))?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| SidecarError::Spawn("stdin pipe unavailable".to_owned()))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| SidecarError::Spawn("stdout pipe unavailable".to_owned()))?;
        let stderr = child
            .stderr
            .take()
            .ok_or_else(|| SidecarError::Spawn("stderr pipe unavailable".to_owned()))?;

        let pending: Pending = Arc::new(Mutex::new(HashMap::new()));
        let alive = Arc::new(AtomicBool::new(true));
        let stderr_log = Arc::new(Mutex::new(Vec::new()));
        let reader_pending = pending.clone();
        let reader_alive = alive.clone();
        let reader_handle = spawn_stdout_reader(stdout, reader_pending, reader_alive);
        let stderr_handle = spawn_stderr_reader(stderr, stderr_log.clone());

        Ok(Self {
            child: Mutex::new(child),
            stdin: Mutex::new(Some(stdin)),
            pending,
            next_id: AtomicU64::new(0),
            request_lock: Mutex::new(()),
            alive,
            stderr_log,
            reader_handle: Mutex::new(Some(reader_handle)),
            stderr_handle: Mutex::new(Some(stderr_handle)),
        })
    }

    pub fn request(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
    ) -> Result<Value, SidecarError> {
        let _request_guard = self
            .request_lock
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if !self.alive.load(Ordering::Relaxed) {
            return Err(self.exited_error());
        }

        let id = self.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        let (sender, receiver) = mpsc::channel();
        self.pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(id, sender);
        let message = json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
            "params": params,
        });
        let mut line = serde_json::to_vec(&message)
            .map_err(|error| SidecarError::Protocol(error.to_string()))?;
        line.push(b'\n');
        {
            let mut stdin = self.stdin.lock().unwrap_or_else(|error| error.into_inner());
            let Some(stdin) = stdin.as_mut() else {
                return Err(self.exited_error());
            };
            if let Err(error) = stdin.write_all(&line).and_then(|()| stdin.flush()) {
                self.pending
                    .lock()
                    .unwrap_or_else(|error| error.into_inner())
                    .remove(&id);
                return Err(SidecarError::Io(error.to_string()));
            }
        }

        match receiver.recv_timeout(timeout) {
            Ok(result) => result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                self.pending
                    .lock()
                    .unwrap_or_else(|error| error.into_inner())
                    .remove(&id);
                Err(SidecarError::Timeout(timeout))
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => Err(self.exited_error()),
        }
    }

    pub fn handshake(
        &self,
        manifest: &PluginManifest,
        token: &str,
        timeout: Duration,
    ) -> Result<(), SidecarError> {
        let result = self.request(
            "plugin.handshake",
            json!({
                "api": 1,
                "token": token,
                "id": manifest.id,
                "version": manifest.version,
                "capabilities": manifest.capabilities,
            }),
            timeout,
        )?;
        if result.get("ok").and_then(Value::as_bool) != Some(true) {
            return Err(SidecarError::Protocol(
                "plugin handshake did not return ok=true".to_owned(),
            ));
        }
        if result.get("api").and_then(Value::as_u64) != Some(1) {
            return Err(SidecarError::Protocol(
                "plugin handshake returned an incompatible api".to_owned(),
            ));
        }
        if result.get("token").and_then(Value::as_str) != Some(token) {
            return Err(SidecarError::Protocol(
                "plugin handshake did not echo the capability token".to_owned(),
            ));
        }
        Ok(())
    }

    pub fn health(&self, token: &str, timeout: Duration) -> Result<(), SidecarError> {
        let result = self.request("plugin.health", json!({"token": token}), timeout)?;
        if result.get("ok").and_then(Value::as_bool) == Some(true) {
            Ok(())
        } else {
            Err(SidecarError::Protocol(format!(
                "plugin health check failed: {result}"
            )))
        }
    }

    pub fn is_alive(&self) -> bool {
        if !self.alive.load(Ordering::Relaxed) {
            return false;
        }
        let mut child = self.child.lock().unwrap_or_else(|error| error.into_inner());
        matches!(child.try_wait(), Ok(None))
    }

    pub fn pid(&self) -> u32 {
        self.child
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .id()
    }

    pub fn stop(&self, timeout: Duration) {
        {
            let mut stdin = self.stdin.lock().unwrap_or_else(|error| error.into_inner());
            drop(stdin.take());
        }
        let deadline = Instant::now() + timeout;
        loop {
            let exited = {
                let mut child = self.child.lock().unwrap_or_else(|error| error.into_inner());
                matches!(child.try_wait(), Ok(Some(_)))
            };
            if exited || Instant::now() >= deadline {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
        {
            let mut child = self.child.lock().unwrap_or_else(|error| error.into_inner());
            if !matches!(child.try_wait(), Ok(Some(_))) {
                let _ = child.kill();
                let _ = child.wait();
            }
        }
        self.alive.store(false, Ordering::Relaxed);
        if let Some(handle) = self
            .reader_handle
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .take()
        {
            let _ = handle.join();
        }
        if let Some(handle) = self
            .stderr_handle
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .take()
        {
            let _ = handle.join();
        }
    }

    pub fn stderr_log(&self) -> String {
        self.stderr_log
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .join("\n")
    }

    fn exited_error(&self) -> SidecarError {
        let log = self.stderr_log();
        if log.is_empty() {
            SidecarError::Exited("plugin process exited".to_owned())
        } else {
            SidecarError::Exited(log)
        }
    }
}

impl Drop for SidecarProcess {
    fn drop(&mut self) {
        self.stop(Duration::from_millis(500));
    }
}

fn spawn_stdout_reader(
    stdout: ChildStdout,
    pending: Pending,
    alive: Arc<AtomicBool>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        let mut reader = BufReader::new(stdout);
        let mut line = String::new();
        loop {
            line.clear();
            match reader.read_line(&mut line) {
                Ok(0) => break,
                Ok(_) => {
                    if !dispatch_line(&line, &pending) {
                        continue;
                    }
                }
                Err(_) => break,
            }
        }
        alive.store(false, Ordering::SeqCst);
        let senders: Vec<_> = pending
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .drain()
            .map(|(_, sender)| sender)
            .collect();
        for sender in senders {
            let _ = sender.send(Err(SidecarError::Exited("plugin stdout closed".to_owned())));
        }
    })
}

fn spawn_stderr_reader(
    stderr: std::process::ChildStderr,
    log: Arc<Mutex<Vec<String>>>,
) -> JoinHandle<()> {
    thread::spawn(move || {
        let mut reader = BufReader::new(stderr);
        let mut line = String::new();
        loop {
            line.clear();
            match reader.read_line(&mut line) {
                Ok(0) => break,
                Ok(_) => {
                    let mut log = log.lock().unwrap_or_else(|error| error.into_inner());
                    if log.len() >= 100 {
                        log.remove(0);
                    }
                    log.push(line.trim_end().to_owned());
                }
                Err(_) => break,
            }
        }
    })
}

fn dispatch_line(line: &str, pending: &Pending) -> bool {
    let Ok(value) = serde_json::from_str::<Value>(line.trim()) else {
        return false;
    };
    let Some(id) = value.get("id").and_then(Value::as_u64) else {
        return false;
    };
    let sender = pending
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .remove(&id);
    let Some(sender) = sender else {
        return false;
    };
    if let Some(error) = value.get("error") {
        let code = error.get("code").and_then(Value::as_i64).unwrap_or(-32603);
        let message = error
            .get("message")
            .and_then(Value::as_str)
            .unwrap_or("plugin rpc error")
            .to_owned();
        let _ = sender.send(Err(SidecarError::Rpc { code, message }));
    } else if let Some(result) = value.get("result") {
        let _ = sender.send(Ok(result.clone()));
    } else {
        let _ = sender.send(Err(SidecarError::Protocol(
            "plugin response has no result or error".to_owned(),
        )));
    }
    true
}

fn add_safe_environment(command: &mut Command) {
    #[cfg(windows)]
    {
        for name in [
            "PATH",
            "PATHEXT",
            "SystemRoot",
            "WINDIR",
            "ComSpec",
            "TEMP",
            "TMP",
        ] {
            if let Ok(value) = std::env::var(name) {
                command.env(name, value);
            }
        }
    }
    #[cfg(not(windows))]
    {
        for name in ["PATH", "HOME", "TMPDIR", "LANG", "LC_ALL"] {
            if let Ok(value) = std::env::var(name) {
                command.env(name, value);
            }
        }
    }
}
