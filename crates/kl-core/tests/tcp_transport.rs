//! Remote transport: `--listen` serves the same protocol over TCP and the
//! `--token` handshake gate rejects clients that do not know the secret.

use serde_json::{Value, json};
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};
use tempfile::TempDir;

struct TcpServer {
    child: Child,
    _temp: TempDir,
    address: String,
}

impl TcpServer {
    // Drop kills and waits; Clippy cannot see that through the returned struct.
    #[allow(clippy::zombie_processes)]
    fn start(token: Option<&str>) -> Self {
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
        let mut command = Command::new(env!("CARGO_BIN_EXE_kl-core"));
        command
            .arg("--serve")
            .arg("--home")
            .arg(&home)
            .arg("--config")
            .arg(temp.path().join("kl.toml"))
            .arg("--listen")
            .arg("127.0.0.1:0")
            .stdout(Stdio::null())
            .stderr(Stdio::null());
        if let Some(token) = token {
            command.arg("--token").arg(token);
        }
        let child = command.spawn().expect("start kl-core tcp server");

        let server_file = home.join("server.json");
        let deadline = Instant::now() + Duration::from_secs(5);
        while Instant::now() < deadline {
            if let Ok(text) = std::fs::read_to_string(&server_file) {
                if let Ok(value) = serde_json::from_str::<Value>(&text) {
                    if value["transport"] == "tcp" {
                        let endpoint = value["endpoint"].as_str().unwrap_or_default();
                        let address = endpoint.trim_start_matches("tcp://").to_owned();
                        return Self {
                            child,
                            _temp: temp,
                            address,
                        };
                    }
                }
            }
            thread::sleep(Duration::from_millis(50));
        }
        panic!("server.json was not created");
    }

    fn connect(&self) -> RpcClient {
        RpcClient::new(TcpStream::connect(&self.address).expect("connect tcp core"))
    }
}

impl Drop for TcpServer {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

struct RpcClient {
    reader: BufReader<TcpStream>,
    writer: TcpStream,
    next_id: i64,
}

impl RpcClient {
    fn new(stream: TcpStream) -> Self {
        let writer = stream.try_clone().unwrap();
        Self {
            reader: BufReader::new(stream),
            writer,
            next_id: 1,
        }
    }

    fn request(&mut self, method: &str, params: Value) -> Value {
        let id = self.next_id;
        self.next_id += 1;
        let request = json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
            "params": params,
        });
        writeln!(self.writer, "{}", serde_json::to_string(&request).unwrap()).unwrap();
        self.writer.flush().unwrap();
        loop {
            let mut line = String::new();
            self.reader.read_line(&mut line).unwrap();
            let value: Value = serde_json::from_str(&line).unwrap();
            if value.get("id") == Some(&json!(id)) {
                return value;
            }
        }
    }
}

fn handshake(token: Option<&str>) -> Value {
    let mut params = json!({
        "client_name": "tcp-test",
        "client_version": "0.1.0",
        "protocol_versions": [1],
    });
    if let Some(token) = token {
        params["token"] = json!(token);
    }
    params
}

#[test]
fn tcp_server_rejects_a_missing_token_and_accepts_the_secret() {
    let server = TcpServer::start(Some("s3cret"));
    let mut client = server.connect();

    let rejected = client.request("kl.handshake", handshake(None));
    assert_eq!(rejected["error"]["code"], -32013, "{rejected}");

    let accepted = client.request("kl.handshake", handshake(Some("s3cret")));
    assert!(accepted.get("error").is_none(), "{accepted}");

    let created = client.request(
        "session.create",
        json!({"workspace": server._temp.path().to_string_lossy()}),
    );
    assert!(created.get("error").is_none(), "{created}");
    let listed = client.request("session.list", json!({}));
    assert_eq!(listed["result"]["sessions"].as_array().unwrap().len(), 1);
}

#[test]
fn tcp_server_without_a_token_serves_loopback() {
    let server = TcpServer::start(None);
    let mut client = server.connect();
    let accepted = client.request("kl.handshake", handshake(None));
    assert!(accepted.get("error").is_none(), "{accepted}");
}

#[test]
fn exposing_tcp_beyond_loopback_requires_a_token() {
    let output = Command::new(env!("CARGO_BIN_EXE_kl-core"))
        .arg("--serve")
        .arg("--listen")
        .arg("0.0.0.0:0")
        .stdin(Stdio::null())
        .output()
        .expect("run kl-core");
    assert_eq!(output.status.code(), Some(2));
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("--token is required"), "{stderr}");
}
