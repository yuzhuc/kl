//! Local multi-client JSON-RPC server.
//!
//! The transport is a Windows named pipe or a Unix-domain socket. The core
//! request loop is shared with stdio; only responses are routed per client,
//! while event notifications are broadcast to connected clients.

use crate::{Core, CoreError, Emitter, ServerContext, unix_ms};
#[cfg(unix)]
use interprocess::local_socket::GenericFilePath;
#[cfg(windows)]
use interprocess::local_socket::GenericNamespaced;
use interprocess::local_socket::{ListenerOptions, prelude::*};
use kl_protocol::{
    CORE_NAME, CORE_VERSION, EventParams, IncomingMessage, PROTOCOL_VERSION, RequestId,
    ServerInfoResult, SessionAttachParams, SessionAttachResult, SessionDetachParams,
    SessionDetachResult, SessionLeaseParams, SessionLeaseResult, error_codes, failure, methods,
    success, validate_envelope,
};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::io::{self, BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicU64, Ordering},
    mpsc,
};
use std::thread;
use std::time::{Duration, Instant};

const DEFAULT_ENDPOINT: &str = "kl-core";
const DEFAULT_LEASE_MS: u64 = 300_000;
const MAX_LEASE_MS: u64 = 86_400_000;

#[derive(Clone)]
struct Broadcaster {
    next_id: Arc<AtomicU64>,
    subscribers: Arc<Mutex<HashMap<u64, mpsc::Sender<String>>>>,
}

impl Broadcaster {
    fn new() -> Self {
        Self {
            next_id: Arc::new(AtomicU64::new(1)),
            subscribers: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    fn subscribe(&self) -> (u64, mpsc::Sender<String>, mpsc::Receiver<String>) {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (sender, receiver) = mpsc::channel();
        self.subscribers
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .insert(id, sender.clone());
        (id, sender, receiver)
    }

    fn unsubscribe(&self, id: u64) {
        self.subscribers
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .remove(&id);
    }

    fn broadcast(&self, line: String) {
        let subscribers: Vec<(u64, mpsc::Sender<String>)> = self
            .subscribers
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .iter()
            .map(|(id, sender)| (*id, sender.clone()))
            .collect();
        let mut closed = Vec::new();
        for (id, sender) in subscribers {
            if sender.send(line.clone()).is_err() {
                closed.push(id);
            }
        }
        if !closed.is_empty() {
            let mut subscribers = self
                .subscribers
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            for id in closed {
                subscribers.remove(&id);
            }
        }
    }

    fn writer(&self) -> BroadcastWriter {
        BroadcastWriter {
            broadcaster: self.clone(),
            pending: Vec::new(),
        }
    }
}

struct BroadcastWriter {
    broadcaster: Broadcaster,
    pending: Vec<u8>,
}

impl Write for BroadcastWriter {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.pending.extend_from_slice(bytes);
        while let Some(position) = self.pending.iter().position(|byte| *byte == b'\n') {
            let line: Vec<u8> = self.pending.drain(..=position).collect();
            self.broadcaster
                .broadcast(String::from_utf8_lossy(&line).into_owned());
        }
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

#[derive(Debug, Clone)]
struct Lease {
    owner: u64,
    expires_at: Instant,
}

#[derive(Default)]
struct LeaseTable {
    leases: HashMap<String, Lease>,
}

impl LeaseTable {
    fn acquire(&mut self, session_id: &str, owner: u64, lease_ms: Option<u64>) -> Result<i64, ()> {
        self.expire();
        if let Some(existing) = self.leases.get(session_id) {
            if existing.owner != owner {
                return Err(());
            }
        }
        let millis = lease_ms.unwrap_or(DEFAULT_LEASE_MS).clamp(1, MAX_LEASE_MS);
        let expires_at_unix_ms = unix_ms() + millis as i64;
        self.leases.insert(
            session_id.to_owned(),
            Lease {
                owner,
                expires_at: Instant::now() + Duration::from_millis(millis),
            },
        );
        Ok(expires_at_unix_ms)
    }

    fn owns(&mut self, session_id: &str, owner: u64) -> bool {
        self.expire();
        self.leases
            .get(session_id)
            .is_some_and(|lease| lease.owner == owner)
    }

    fn release_owner(&mut self, owner: u64) {
        self.leases.retain(|_, lease| lease.owner != owner);
    }

    fn release(&mut self, session_id: &str, owner: u64) {
        if self
            .leases
            .get(session_id)
            .is_some_and(|lease| lease.owner == owner)
        {
            self.leases.remove(session_id);
        }
    }

    fn expire(&mut self) {
        let now = Instant::now();
        self.leases.retain(|_, lease| lease.expires_at > now);
    }
}

pub fn run_server(
    config_path: Option<PathBuf>,
    home: Option<PathBuf>,
    endpoint: Option<String>,
) -> Result<(), CoreError> {
    let context = ServerContext::resolve(config_path, home);
    std::fs::create_dir_all(&context.home)?;
    let endpoint_name = endpoint
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| DEFAULT_ENDPOINT.to_owned());
    let (listener, transport, advertised_endpoint) =
        create_listener(&context.home, &endpoint_name)?;
    let broadcaster = Broadcaster::new();
    let core = Arc::new(Mutex::new(Core::new(
        context.clone(),
        Emitter::new(Box::new(broadcaster.writer())),
    )?));
    let leases = Arc::new(Mutex::new(LeaseTable::default()));
    let server_info = ServerInfoResult {
        server_name: CORE_NAME.to_owned(),
        server_version: CORE_VERSION.to_owned(),
        protocol_version: PROTOCOL_VERSION,
        transport,
        endpoint: advertised_endpoint.clone(),
        pid: std::process::id(),
        started_at_unix_ms: unix_ms(),
    };
    let server_file = context.home.join("server.json");
    std::fs::write(
        &server_file,
        serde_json::to_vec_pretty(&server_info).map_err(|error| {
            CoreError::Io(io::Error::other(format!(
                "server info serialization: {error}"
            )))
        })?,
    )?;

    eprintln!(
        "kl-core server listening on {} ({})",
        advertised_endpoint,
        server_file.display()
    );

    for connection in listener.incoming() {
        let stream = match connection {
            Ok(stream) => stream,
            Err(error) => {
                eprintln!("kl-core server accept failed: {error}");
                continue;
            }
        };
        let (client_id, response_tx, receiver) = broadcaster.subscribe();
        let (reader, writer) = stream.split();
        let subscriptions = Arc::new(Mutex::new(HashSet::new()));
        let writer_subscriptions = Arc::clone(&subscriptions);
        let writer_task = thread::spawn(move || {
            let mut writer = writer;
            for line in receiver {
                if !should_forward(&line, &writer_subscriptions) {
                    continue;
                }
                if writer.write_all(line.as_bytes()).is_err() {
                    break;
                }
            }
        });
        let core = Arc::clone(&core);
        let broadcaster = broadcaster.clone();
        let client_leases = Arc::clone(&leases);
        let client_info = server_info.clone();
        thread::spawn(move || {
            handle_client(
                reader,
                response_tx,
                core,
                client_leases,
                client_info,
                client_id,
                subscriptions,
            );
            broadcaster.unsubscribe(client_id);
            let _ = writer_task.join();
        });
    }
    Ok(())
}

fn handle_client(
    receiver: interprocess::local_socket::RecvHalf,
    response_tx: mpsc::Sender<String>,
    core: Arc<Mutex<Core>>,
    leases: Arc<Mutex<LeaseTable>>,
    server_info: ServerInfoResult,
    client_id: u64,
    attached: Arc<Mutex<HashSet<String>>>,
) {
    let mut reader = BufReader::new(receiver);
    let mut line = String::new();
    let mut handshaken = false;
    let mut current_session: Option<String> = None;

    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) => break,
            Ok(_) => {}
            Err(error) => {
                eprintln!("kl-core client read failed: {error}");
                break;
            }
        }
        if line.trim().is_empty() {
            continue;
        }

        let message: IncomingMessage = match serde_json::from_str(&line) {
            Ok(message) => message,
            Err(error) => {
                let _ = send_response(
                    &response_tx,
                    failure(
                        None,
                        error_codes::PARSE_ERROR,
                        format!("invalid JSON: {error}"),
                    ),
                );
                continue;
            }
        };
        if let Err(error) = validate_envelope(&message) {
            let _ = send_response(
                &response_tx,
                failure(message.id.clone(), error.code, error.message),
            );
            continue;
        }
        let Some(id) = message.id.clone() else {
            continue;
        };
        let method = message.method.clone().unwrap_or_default();

        let public_method = matches!(
            method.as_str(),
            methods::HANDSHAKE | methods::PING | methods::DOCTOR | methods::SERVER_INFO
        );
        let response = if !handshaken && !public_method {
            failure(
                Some(id),
                error_codes::NOT_INITIALIZED,
                "handshake required before this method",
            )
        } else {
            match method.as_str() {
                methods::SERVER_INFO => success(
                    id,
                    serde_json::to_value(&server_info).unwrap_or(Value::Null),
                ),
                methods::SESSION_ATTACH => {
                    handle_attach(id, &message.params, &core, &attached, &leases, client_id)
                }
                methods::SESSION_DETACH => {
                    handle_detach(id, &message.params, &attached, &leases, client_id)
                }
                methods::SESSION_LEASE => {
                    handle_lease(id, &message.params, &core, &leases, client_id)
                }
                _ => {
                    if let Some(session_id) = lease_required_session(&method, &message.params)
                        && !leases
                            .lock()
                            .unwrap_or_else(|error| error.into_inner())
                            .owns(&session_id, client_id)
                    {
                        failure(
                            Some(id),
                            error_codes::LEASE_REQUIRED,
                            format!("lease required for {session_id}"),
                        )
                    } else {
                        let response = {
                            let mut core = core.lock().unwrap_or_else(|error| error.into_inner());
                            let previous_handshaken = core.handshaken;
                            let previous_session = core.current_session.clone();
                            core.handshaken = handshaken;
                            core.current_session = current_session.clone();
                            let response = core.handle_request(&line);
                            handshaken = core.handshaken;
                            current_session = core.current_session.clone();
                            core.handshaken = previous_handshaken;
                            core.current_session = previous_session;
                            response
                        };
                        response.unwrap_or_else(|| {
                            failure(
                                Some(id),
                                error_codes::INVALID_REQUEST,
                                "request has no response",
                            )
                        })
                    }
                }
            }
        };
        let _ = send_response(&response_tx, response);
    }

    leases
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .release_owner(client_id);
}

fn handle_attach(
    id: RequestId,
    params: &Value,
    core: &Arc<Mutex<Core>>,
    attached: &Arc<Mutex<HashSet<String>>>,
    leases: &Arc<Mutex<LeaseTable>>,
    client_id: u64,
) -> Value {
    let params: SessionAttachParams = match serde_json::from_value(params.clone()) {
        Ok(params) => params,
        Err(error) => return failure(Some(id), error_codes::INVALID_PARAMS, error.to_string()),
    };
    let core = core.lock().unwrap_or_else(|error| error.into_inner());
    if core.store.load(&params.session_id).is_err() {
        return failure(
            Some(id),
            error_codes::SESSION_NOT_FOUND,
            format!("session not found: {}", params.session_id),
        );
    }
    let events = match core.store.read_events(&params.session_id) {
        Ok(events) => events,
        Err(error) => return failure(Some(id), error_codes::INTERNAL_ERROR, error.to_string()),
    };
    let after_seq = params.after_seq.unwrap_or(0);
    let last_seq = events.last().map(|event| event.seq).unwrap_or(0);
    let replay = events
        .into_iter()
        .filter(|event| event.seq > after_seq)
        .map(|event| EventParams {
            session_id: params.session_id.clone(),
            seq: event.seq,
            event: event.event,
        })
        .collect();
    attached
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .insert(params.session_id.clone());
    let lease_held = leases
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .owns(&params.session_id, client_id);
    success(
        id,
        serde_json::to_value(SessionAttachResult {
            session_id: params.session_id,
            replay,
            last_seq,
            lease_held,
        })
        .unwrap_or(Value::Null),
    )
}

fn handle_detach(
    id: RequestId,
    params: &Value,
    attached: &Arc<Mutex<HashSet<String>>>,
    leases: &Arc<Mutex<LeaseTable>>,
    client_id: u64,
) -> Value {
    let params: SessionDetachParams = match serde_json::from_value(params.clone()) {
        Ok(params) => params,
        Err(error) => return failure(Some(id), error_codes::INVALID_PARAMS, error.to_string()),
    };
    attached
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .remove(&params.session_id);
    leases
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .release(&params.session_id, client_id);
    success(
        id,
        serde_json::to_value(SessionDetachResult {
            session_id: params.session_id,
            detached: true,
        })
        .unwrap_or(Value::Null),
    )
}

fn handle_lease(
    id: RequestId,
    params: &Value,
    core: &Arc<Mutex<Core>>,
    leases: &Arc<Mutex<LeaseTable>>,
    client_id: u64,
) -> Value {
    let params: SessionLeaseParams = match serde_json::from_value(params.clone()) {
        Ok(params) => params,
        Err(error) => return failure(Some(id), error_codes::INVALID_PARAMS, error.to_string()),
    };
    if core
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .store
        .load(&params.session_id)
        .is_err()
    {
        return failure(
            Some(id),
            error_codes::SESSION_NOT_FOUND,
            format!("session not found: {}", params.session_id),
        );
    }
    match leases
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .acquire(&params.session_id, client_id, params.lease_ms)
    {
        Ok(expires_at_unix_ms) => success(
            id,
            serde_json::to_value(SessionLeaseResult {
                session_id: params.session_id,
                expires_at_unix_ms,
            })
            .unwrap_or(Value::Null),
        ),
        Err(()) => failure(
            Some(id),
            error_codes::SESSION_BUSY,
            format!("session.busy: {}", params.session_id),
        ),
    }
}

fn lease_required_session(method: &str, params: &Value) -> Option<String> {
    if !matches!(
        method,
        methods::TURN_START | methods::TURN_CANCEL | methods::APPROVAL_RESPOND
    ) {
        return None;
    }
    params
        .get("session_id")
        .and_then(Value::as_str)
        .map(str::to_owned)
}

fn should_forward(line: &str, subscriptions: &Arc<Mutex<HashSet<String>>>) -> bool {
    let Ok(value) = serde_json::from_str::<Value>(line) else {
        return true;
    };
    if value.get("method").and_then(Value::as_str) != Some(methods::EVENT) {
        return true;
    }
    let Some(session_id) = value
        .get("params")
        .and_then(|params| params.get("session_id"))
        .and_then(Value::as_str)
    else {
        return false;
    };
    subscriptions
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .contains(session_id)
}
fn send_response(tx: &mpsc::Sender<String>, value: Value) -> io::Result<()> {
    let mut line = serde_json::to_vec(&value).map_err(io::Error::other)?;
    line.push(b'\n');
    tx.send(String::from_utf8_lossy(&line).into_owned())
        .map_err(|_| io::Error::new(io::ErrorKind::BrokenPipe, "client writer closed"))
}

fn create_listener(
    home: &Path,
    endpoint: &str,
) -> io::Result<(LocalSocketListener, String, String)> {
    #[cfg(windows)]
    let _ = home;
    #[cfg(windows)]
    {
        let name = endpoint.to_ns_name::<GenericNamespaced>()?;
        let listener = ListenerOptions::new()
            .name(name)
            // Existing endpoints fail closed; the server must not steal a live listener.
            .create_sync()?;
        Ok((listener, "named-pipe".to_owned(), endpoint.to_owned()))
    }
    #[cfg(unix)]
    {
        let path = if Path::new(endpoint).is_absolute() {
            PathBuf::from(endpoint)
        } else {
            home.join(format!("{endpoint}.sock"))
        };
        let text = path.to_string_lossy().into_owned();
        let name = text.as_str().to_fs_name::<GenericFilePath>()?;
        let listener = ListenerOptions::new()
            .name(name)
            // Existing endpoints fail closed; the server must not steal a live listener.
            .create_sync()?;
        Ok((listener, "unix-socket".to_owned(), text))
    }
}
