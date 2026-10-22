#[cfg(unix)]
use interprocess::local_socket::GenericFilePath;
#[cfg(windows)]
use interprocess::local_socket::GenericNamespaced;
use interprocess::local_socket::prelude::*;
use serde_json::{Value, json};
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};
use tempfile::TempDir;

struct TestServer {
    child: Child,
    temp: TempDir,
    home: PathBuf,
    endpoint: String,
}

impl TestServer {
    fn start() -> Self {
        let temp = tempfile::tempdir().unwrap();
        let home = temp.path().join(".kl");
        std::fs::create_dir_all(&home).unwrap();
        std::fs::write(temp.path().join("script.json"), r#"[{"text":"hello"}]"#).unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
"#,
        )
        .unwrap();
        let endpoint = format!("kl-test-{}", uuid::Uuid::new_v4());
        let child = Command::new(env!("CARGO_BIN_EXE_kl-core"))
            .arg("--serve")
            .arg("--home")
            .arg(&home)
            .arg("--config")
            .arg(temp.path().join("kl.toml"))
            .arg("--socket")
            .arg(&endpoint)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("start kl-core server");
        let server = Self {
            child,
            temp,
            home,
            endpoint,
        };
        let deadline = Instant::now() + Duration::from_secs(5);
        while Instant::now() < deadline {
            if server.home.join("server.json").exists() {
                return server;
            }
            thread::sleep(Duration::from_millis(50));
        }
        panic!("server.json was not created");
    }

    fn workspace(&self) -> String {
        self.temp.path().to_string_lossy().into_owned()
    }

    fn connect(&self) -> RpcClient {
        RpcClient::new(connect_endpoint(&self.home, &self.endpoint))
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

struct RpcClient {
    reader: BufReader<interprocess::local_socket::RecvHalf>,
    writer: interprocess::local_socket::SendHalf,
    next_id: i64,
}

impl RpcClient {
    fn new(stream: LocalSocketStream) -> Self {
        let (reader, writer) = stream.split();
        Self {
            reader: BufReader::new(reader),
            writer,
            next_id: 0,
        }
    }

    fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        let request = json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
            "params": params,
        });
        self.writer
            .write_all(request.to_string().as_bytes())
            .unwrap();
        self.writer.write_all(b"\n").unwrap();
        self.writer.flush().unwrap();

        loop {
            let mut line = String::new();
            assert_ne!(
                self.reader.read_line(&mut line).unwrap(),
                0,
                "server closed"
            );
            let message: Value = serde_json::from_str(&line).unwrap();
            if message.get("id").and_then(Value::as_i64) == Some(id) {
                return message;
            }
            assert_eq!(message.get("method").and_then(Value::as_str), Some("event"));
        }
    }

    fn handshake(&mut self) -> Value {
        self.request(
            "kl.handshake",
            json!({
                "client_name": "server-transport-test",
                "client_version": "0.1.0",
                "protocol_versions": [1],
                "features": []
            }),
        )
    }
}

#[cfg(windows)]
fn connect_endpoint(_home: &Path, endpoint: &str) -> LocalSocketStream {
    let name = endpoint.to_ns_name::<GenericNamespaced>().unwrap();
    LocalSocketStream::connect(name).unwrap()
}

#[cfg(unix)]
fn connect_endpoint(home: &Path, endpoint: &str) -> LocalSocketStream {
    let path = home.join(format!("{endpoint}.sock"));
    let text = path.to_string_lossy().into_owned();
    let name = text.as_str().to_fs_name::<GenericFilePath>().unwrap();
    LocalSocketStream::connect(name).unwrap()
}

#[test]
fn two_clients_attach_and_lease_one_writer() {
    let server = TestServer::start();
    let mut first = server.connect();
    let mut second = server.connect();
    assert_eq!(first.handshake()["result"]["protocol_version"], 1);
    assert_eq!(second.handshake()["result"]["protocol_version"], 1);

    let info = first.request("server.info", json!({}));
    assert!(matches!(
        info["result"]["transport"].as_str(),
        Some("named-pipe" | "unix-socket")
    ));

    let created = first.request("session.create", json!({"workspace": server.workspace()}));
    let session_id = created["result"]["session_id"].as_str().unwrap().to_owned();

    let attached = second.request(
        "session.attach",
        json!({"session_id": session_id, "after_seq": 0}),
    );
    assert!(attached["result"]["last_seq"].as_u64().unwrap() >= 1);
    assert!(!attached["result"]["replay"].as_array().unwrap().is_empty());

    assert!(
        first
            .request(
                "session.lease",
                json!({"session_id": session_id, "lease_ms": 30000})
            )
            .get("error")
            .is_none()
    );
    let busy = second.request(
        "session.lease",
        json!({"session_id": session_id, "lease_ms": 30000}),
    );
    assert_eq!(busy["error"]["code"], -32008);

    drop(first);
    let deadline = Instant::now() + Duration::from_secs(3);
    loop {
        let lease = second.request(
            "session.lease",
            json!({"session_id": session_id, "lease_ms": 30000}),
        );
        if lease.get("error").is_none() {
            break;
        }
        assert_eq!(lease["error"]["code"], -32008);
        assert!(Instant::now() < deadline, "lease was not released");
        thread::sleep(Duration::from_millis(100));
    }

    let detached = second.request("session.detach", json!({"session_id": session_id}));
    assert_eq!(detached["result"]["detached"], true);
}

#[test]
fn turn_requires_a_lease() {
    let server = TestServer::start();
    let mut client = server.connect();
    client.handshake();
    let created = client.request("session.create", json!({"workspace": server.workspace()}));
    let session_id = created["result"]["session_id"].as_str().unwrap().to_owned();
    let turn = client.request(
        "turn.start",
        json!({"session_id": session_id, "prompt": "hello"}),
    );
    assert_eq!(turn["error"]["code"], -32009);
}
