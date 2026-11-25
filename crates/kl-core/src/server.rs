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
    CORE_NAME, CORE_VERSION, CoreEvent, EventParams, IncomingMessage, PROTOCOL_VERSION, RequestId,
    ServerInfoResult, SessionAttachParams, SessionAttachResult, SessionDetachParams,
    SessionDetachResult, SessionLeaseParams, SessionLeaseResult, error_codes, failure, methods,
    success, validate_envelope,
};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::io::{self, BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    mpsc,
};
use std::thread;
use std::time::{Duration, Instant};

const DEFAULT_ENDPOINT: &str = "kl-core";
const DEFAULT_LEASE_MS: u64 = 300_000;
const MAX_LEASE_MS: u64 = 86_400_000;
const TLS_HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
const TLS_WRITE_TIMEOUT: Duration = Duration::from_secs(30);
const MAX_PENDING_LINES: usize = 1024;
const MAX_TCP_CONNECTIONS: usize = 64;

/// Loads a certificate chain and private key, prints the leaf fingerprint and
/// returns the server config used for `--listen --tls-cert/--tls-key`.
fn load_tls_config(cert_path: &Path, key_path: &Path) -> Result<rustls::ServerConfig, CoreError> {
    use rustls::pki_types::{CertificateDer, PrivateKeyDer};
    use sha2::{Digest, Sha256};

    let cert_bytes = std::fs::read(cert_path)?;
    let cert_text = String::from_utf8_lossy(&cert_bytes);
    let certificates: Vec<CertificateDer<'static>> = if cert_text.contains("BEGIN CERTIFICATE") {
        pem_blocks(&cert_text, "CERTIFICATE")
            .into_iter()
            .map(CertificateDer::from)
            .collect()
    } else {
        vec![CertificateDer::from(cert_bytes)]
    };
    if certificates.is_empty() {
        return Err(CoreError::Io(io::Error::other(format!(
            "no certificate found in {}",
            cert_path.display()
        ))));
    }

    let key_text = std::fs::read(key_path)?;
    let key_text = String::from_utf8_lossy(&key_text).into_owned();
    let key = if let Some(bytes) = pem_blocks(&key_text, "PRIVATE KEY").into_iter().next() {
        PrivateKeyDer::Pkcs8(bytes.into())
    } else if let Some(bytes) = pem_blocks(&key_text, "RSA PRIVATE KEY").into_iter().next() {
        PrivateKeyDer::Pkcs1(bytes.into())
    } else if let Some(bytes) = pem_blocks(&key_text, "EC PRIVATE KEY").into_iter().next() {
        PrivateKeyDer::Sec1(bytes.into())
    } else {
        return Err(CoreError::Io(io::Error::other(format!(
            "no private key found in {}; export a PEM PKCS#8 key with openssl",
            key_path.display()
        ))));
    };

    let config = rustls::ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(certificates.clone(), key)
        .map_err(|error| io::Error::other(format!("TLS certificate: {error}")))?;
    let fingerprint: String = Sha256::digest(certificates[0].as_ref())
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect();
    eprintln!("kl-core TLS certificate SHA-256 fingerprint: {fingerprint}");
    Ok(config)
}

fn pem_blocks(text: &str, label: &str) -> Vec<Vec<u8>> {
    use base64::Engine;
    let begin = format!("-----BEGIN {label}-----");
    let end = format!("-----END {label}-----");
    let mut blocks = Vec::new();
    let mut rest = text;
    while let Some(start) = rest.find(&begin) {
        let after = &rest[start + begin.len()..];
        let Some(finish) = after.find(&end) else {
            break;
        };
        let body: String = after[..finish]
            .chars()
            .filter(|character| !character.is_whitespace())
            .collect();
        if let Ok(bytes) = base64::engine::general_purpose::STANDARD.decode(body) {
            blocks.push(bytes);
        }
        rest = &after[finish + end.len()..];
    }
    blocks
}

/// One TLS connection shared by the reader and writer threads. The socket is
/// nonblocking so a blocked writer never starves the reader; each retry
/// releases the lock for a couple of milliseconds.
struct TlsState {
    stream: Mutex<Option<rustls::StreamOwned<rustls::ServerConnection, TcpStream>>>,
    closed: AtomicBool,
}

impl TlsState {
    fn stream_closed() -> io::Error {
        io::Error::new(io::ErrorKind::BrokenPipe, "TLS connection closed")
    }

    fn close(&self) {
        if self.closed.swap(true, Ordering::SeqCst) {
            return;
        }
        let mut guard = self
            .stream
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if let Some(mut stream) = guard.take() {
            stream.conn.send_close_notify();
            let _ = stream.conn.complete_io(&mut stream.sock);
        }
    }
}

struct TlsReader {
    state: Arc<TlsState>,
    finished: bool,
}

impl io::Read for TlsReader {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.finished {
            return Ok(0);
        }
        loop {
            if self.state.closed.load(Ordering::SeqCst) {
                self.finished = true;
                return Ok(0);
            }
            let mut guard = self
                .state
                .stream
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let Some(stream) = guard.as_mut() else {
                self.finished = true;
                return Ok(0);
            };
            match stream.read(buffer) {
                Ok(0) => {
                    drop(guard);
                    self.finished = true;
                    self.state.close();
                    return Ok(0);
                }
                Ok(length) => return Ok(length),
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    drop(guard);
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => {
                    drop(guard);
                    self.finished = true;
                    self.state.close();
                    return Ok(0);
                }
                Err(error) => {
                    drop(guard);
                    self.finished = true;
                    self.state.close();
                    return Err(error);
                }
            }
        }
    }
}

struct TlsWriter {
    state: Arc<TlsState>,
}

impl Write for TlsWriter {
    fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
        let deadline = Instant::now() + TLS_WRITE_TIMEOUT;
        loop {
            if self.state.closed.load(Ordering::SeqCst) {
                return Err(TlsState::stream_closed());
            }
            if Instant::now() >= deadline {
                self.state.close();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "TLS write timed out",
                ));
            }
            let mut guard = self
                .state
                .stream
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let Some(stream) = guard.as_mut() else {
                return Err(TlsState::stream_closed());
            };
            match stream.write(buffer) {
                Ok(length) if length > 0 => return Ok(length),
                Ok(_) => {
                    drop(guard);
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    drop(guard);
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) => {
                    drop(guard);
                    self.state.close();
                    return Err(error);
                }
            }
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        let deadline = Instant::now() + TLS_WRITE_TIMEOUT;
        loop {
            if self.state.closed.load(Ordering::SeqCst) {
                return Err(TlsState::stream_closed());
            }
            if Instant::now() >= deadline {
                self.state.close();
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "TLS flush timed out",
                ));
            }
            let mut guard = self
                .state
                .stream
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let Some(stream) = guard.as_mut() else {
                return Err(TlsState::stream_closed());
            };
            match stream.flush() {
                Ok(()) => return Ok(()),
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                    drop(guard);
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) => {
                    drop(guard);
                    self.state.close();
                    return Err(error);
                }
            }
        }
    }
}

fn accept_tls(
    config: Arc<rustls::ServerConfig>,
    socket: TcpStream,
) -> io::Result<(TlsReader, TlsWriter)> {
    socket.set_nonblocking(true)?;
    let connection = rustls::ServerConnection::new(config)
        .map_err(|error| io::Error::other(error.to_string()))?;
    let mut stream = rustls::StreamOwned::new(connection, socket);
    let deadline = Instant::now() + TLS_HANDSHAKE_TIMEOUT;
    while stream.conn.is_handshaking() {
        match stream.conn.complete_io(&mut stream.sock) {
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                if Instant::now() >= deadline {
                    return Err(io::Error::new(
                        io::ErrorKind::TimedOut,
                        "TLS handshake timed out",
                    ));
                }
                thread::sleep(Duration::from_millis(2));
            }
            Err(error) => return Err(error),
        }
    }
    let state = Arc::new(TlsState {
        stream: Mutex::new(Some(stream)),
        closed: AtomicBool::new(false),
    });
    Ok((
        TlsReader {
            state: Arc::clone(&state),
            finished: false,
        },
        TlsWriter { state },
    ))
}

#[derive(Clone)]
struct Broadcaster {
    next_id: Arc<AtomicU64>,
    subscribers: Arc<Mutex<HashMap<u64, mpsc::SyncSender<String>>>>,
}

impl Broadcaster {
    fn new() -> Self {
        Self {
            next_id: Arc::new(AtomicU64::new(1)),
            subscribers: Arc::new(Mutex::new(HashMap::new())),
        }
    }

    fn subscribe(&self) -> (u64, mpsc::SyncSender<String>, mpsc::Receiver<String>) {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (sender, receiver) = mpsc::sync_channel(MAX_PENDING_LINES);
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
        let subscribers: Vec<(u64, mpsc::SyncSender<String>)> = self
            .subscribers
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .iter()
            .map(|(id, sender)| (*id, sender.clone()))
            .collect();
        let mut closed = Vec::new();
        for (id, sender) in subscribers {
            if sender.try_send(line.clone()).is_err() {
                eprintln!("kl-core removing slow client {id} from event broadcast");
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

    fn held_by_other(&mut self, session_id: &str, owner: u64) -> bool {
        self.expire();
        self.leases
            .get(session_id)
            .is_some_and(|lease| lease.owner != owner)
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
    let state = start_server(context, transport, advertised_endpoint, None)?;
    for connection in listener.incoming() {
        let stream = match connection {
            Ok(stream) => stream,
            Err(error) => {
                eprintln!("kl-core server accept failed: {error}");
                continue;
            }
        };
        let (reader, writer) = stream.split();
        spawn_connection(&state, BufReader::new(reader), writer, None);
    }
    Ok(())
}

/// Serves the protocol over TCP for remote clients, e.g.
/// `kl-core --serve --listen 0.0.0.0:8787 --token <secret>`.
pub fn run_tcp_server(
    config_path: Option<PathBuf>,
    home: Option<PathBuf>,
    address: &str,
    token: Option<String>,
    tls: Option<(PathBuf, PathBuf)>,
) -> Result<(), CoreError> {
    let context = ServerContext::resolve(config_path, home);
    std::fs::create_dir_all(&context.home)?;
    let tls_config = match &tls {
        Some((certificate, key)) => Some(Arc::new(load_tls_config(certificate, key)?)),
        None => None,
    };
    let listener = TcpListener::bind(address)?;
    let bound = listener.local_addr()?.to_string();
    let (scheme, transport) = if tls_config.is_some() {
        ("tls", "tls")
    } else {
        ("tcp", "tcp")
    };
    let endpoint = format!("{scheme}://{bound}");
    let state = start_server(context, transport.to_owned(), endpoint, token)?;
    let active = Arc::new(AtomicUsize::new(0));
    for connection in listener.incoming() {
        let stream = match connection {
            Ok(stream) => stream,
            Err(error) => {
                eprintln!("kl-core server accept failed: {error}");
                continue;
            }
        };
        let peer = stream
            .peer_addr()
            .map(|address| address.to_string())
            .unwrap_or_else(|_| "unknown".to_owned());
        if active.fetch_add(1, Ordering::Relaxed) >= MAX_TCP_CONNECTIONS {
            active.fetch_sub(1, Ordering::Relaxed);
            eprintln!("kl-core connection limit ({MAX_TCP_CONNECTIONS}) reached; rejecting {peer}");
            continue;
        }
        let slot = ConnectionSlot(Arc::clone(&active));
        if let Some(config) = tls_config.clone() {
            let connection_state = state.clone();
            thread::spawn(move || match accept_tls(config, stream) {
                Ok((reader, writer)) => {
                    spawn_connection(
                        &connection_state,
                        BufReader::new(reader),
                        writer,
                        Some(slot),
                    );
                }
                Err(error) => {
                    eprintln!("kl-core TLS handshake failed for {peer}: {error}");
                    drop(slot);
                }
            });
            continue;
        }
        let writer = match stream.try_clone() {
            Ok(writer) => writer,
            Err(error) => {
                eprintln!("kl-core server connection failed for {peer}: {error}");
                drop(slot);
                continue;
            }
        };
        spawn_connection(&state, BufReader::new(stream), writer, Some(slot));
    }
    Ok(())
}

#[derive(Clone)]
struct ServerState {
    core: Arc<Mutex<Core>>,
    broadcaster: Broadcaster,
    leases: Arc<Mutex<LeaseTable>>,
    server_info: ServerInfoResult,
    token: Option<String>,
}

fn start_server(
    context: ServerContext,
    transport: String,
    endpoint: String,
    token: Option<String>,
) -> Result<ServerState, CoreError> {
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
        endpoint: endpoint.clone(),
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
        endpoint,
        server_file.display()
    );

    Ok(ServerState {
        core,
        broadcaster,
        leases,
        server_info,
        token,
    })
}

struct ConnectionSlot(Arc<AtomicUsize>);

impl Drop for ConnectionSlot {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::Relaxed);
    }
}

fn spawn_connection<R: BufRead + Send + 'static, W: Write + Send + 'static>(
    state: &ServerState,
    reader: R,
    writer: W,
    slot: Option<ConnectionSlot>,
) {
    let (client_id, response_tx, receiver) = state.broadcaster.subscribe();
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
            // rustls' Write may report success after swallowing a transport
            // error; flush drains the TLS buffer and surfaces it.
            if writer.flush().is_err() {
                break;
            }
        }
    });
    let core = Arc::clone(&state.core);
    let broadcaster = state.broadcaster.clone();
    let client_leases = Arc::clone(&state.leases);
    let client_info = state.server_info.clone();
    let token = state.token.clone();
    thread::spawn(move || {
        handle_client(
            reader,
            ClientConnection {
                response_tx,
                core,
                leases: client_leases,
                server_info: client_info,
                client_id,
                attached: subscriptions,
                token,
            },
        );
        broadcaster.unsubscribe(client_id);
        let _ = writer_task.join();
        drop(slot);
    });
}

struct ClientConnection {
    response_tx: mpsc::SyncSender<String>,
    core: Arc<Mutex<Core>>,
    leases: Arc<Mutex<LeaseTable>>,
    server_info: ServerInfoResult,
    client_id: u64,
    attached: Arc<Mutex<HashSet<String>>>,
    token: Option<String>,
}

fn handle_client<R: BufRead>(receiver: R, connection: ClientConnection) {
    let ClientConnection {
        response_tx,
        core,
        leases,
        server_info,
        client_id,
        attached,
        token,
    } = connection;
    let mut reader = receiver;
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

        if !handshaken && method == methods::HANDSHAKE {
            if let Some(required) = token.as_deref() {
                if message.params.get("token").and_then(Value::as_str) != Some(required) {
                    let _ = send_response(
                        &response_tx,
                        failure(
                            Some(id),
                            error_codes::UNAUTHORIZED,
                            "invalid or missing access token",
                        ),
                    );
                    continue;
                }
            }
        }

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
                    let missing_compact_session = method == methods::SESSION_COMPACT
                        && message
                            .params
                            .get("session_id")
                            .and_then(Value::as_str)
                            .is_none_or(|session_id| session_id.trim().is_empty())
                        && current_session
                            .as_deref()
                            .is_none_or(|session_id| session_id.is_empty());
                    if missing_compact_session {
                        failure(
                            Some(id),
                            error_codes::INVALID_PARAMS,
                            "session_id is required for session.compact over the server",
                        )
                    } else {
                        let requirement = lease_required_session(
                            &method,
                            &message.params,
                            current_session.as_deref(),
                        );
                        let allowed = match &requirement {
                            Some((session_id, must_own)) => {
                                let mut leases =
                                    leases.lock().unwrap_or_else(|error| error.into_inner());
                                if *must_own {
                                    leases.owns(session_id, client_id)
                                } else {
                                    !leases.held_by_other(session_id, client_id)
                                }
                            }
                            None => true,
                        };
                        if !allowed {
                            let session_id = requirement
                                .map(|(session_id, _)| session_id)
                                .unwrap_or_default();
                            failure(
                                Some(id),
                                error_codes::LEASE_REQUIRED,
                                format!("lease required for {session_id}"),
                            )
                        } else {
                            let response = {
                                let mut core =
                                    core.lock().unwrap_or_else(|error| error.into_inner());
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
    let last_seq = events.iter().map(|event| event.seq).max().unwrap_or(0);
    let rewinds: Vec<(u64, u64)> = events
        .iter()
        .filter_map(|event| match &event.event {
            CoreEvent::ConversationRewound { from_seq } => Some((*from_seq, event.seq)),
            _ => None,
        })
        .collect();
    let replay = events
        .into_iter()
        .filter(|event| event.seq > after_seq)
        .filter(|event| {
            !rewinds
                .iter()
                .any(|(from, marker)| event.seq >= *from && event.seq < *marker)
        })
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

/// Returns the session a request must be gated on, and whether the caller must
/// own the lease (`true`) or merely must not be blocked by another owner.
fn lease_required_session(
    method: &str,
    params: &Value,
    current_session: Option<&str>,
) -> Option<(String, bool)> {
    let must_own = matches!(
        method,
        methods::TURN_START | methods::TURN_CANCEL | methods::APPROVAL_RESPOND
    );
    // session.fork only reads the source and creates a new session, so it is
    // deliberately not gated here.
    let session_id = if matches!(method, methods::SESSION_COMPACT) {
        params
            .get("session_id")
            .and_then(Value::as_str)
            .or(current_session)
    } else if must_own
        || matches!(
            method,
            methods::SESSION_RENAME | methods::SESSION_DELETE | methods::SESSION_ARCHIVE
        )
    {
        params.get("session_id").and_then(Value::as_str)
    } else {
        return None;
    };
    session_id.map(|session_id| (session_id.to_owned(), must_own))
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
fn send_response(tx: &mpsc::SyncSender<String>, value: Value) -> io::Result<()> {
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

#[cfg(test)]
mod tests;
