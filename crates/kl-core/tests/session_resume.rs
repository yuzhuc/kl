use serde_json::{Value, json};
use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

struct CoreProcess {
    child: Child,
    stdin: Option<ChildStdin>,
    stdout: BufReader<ChildStdout>,
    events: VecDeque<Value>,
    next_id: u64,
}

impl CoreProcess {
    fn start(config: &Path, home: &Path) -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_kl-core"))
            .arg("--config")
            .arg(config)
            .arg("--home")
            .arg(home)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .expect("spawn kl-core");
        let stdin = child.stdin.take();
        let stdout = BufReader::new(child.stdout.take().expect("core stdout"));
        Self {
            child,
            stdin,
            stdout,
            events: VecDeque::new(),
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
        writeln!(
            self.stdin.as_mut().expect("core stdin"),
            "{}",
            serde_json::to_string(&request).unwrap()
        )
        .unwrap();
        self.stdin.as_mut().unwrap().flush().unwrap();

        loop {
            let value = self.read_line();
            if event_type(&value).is_some() {
                self.events.push_back(value);
                continue;
            }
            if value.get("id") == Some(&json!(id)) {
                return value;
            }
        }
    }

    fn wait_for_turn(&mut self) -> Value {
        loop {
            if let Some(index) = self.events.iter().position(|value| {
                matches!(
                    event_type(value),
                    Some("turn.completed") | Some("turn.failed")
                )
            }) {
                return self.events.remove(index).unwrap();
            }
            let value = self.read_line();
            if matches!(
                event_type(&value),
                Some("turn.completed") | Some("turn.failed")
            ) {
                return value;
            }
            if event_type(&value).is_some() {
                self.events.push_back(value);
            }
        }
    }

    fn read_line(&mut self) -> Value {
        let mut line = String::new();
        let count = self.stdout.read_line(&mut line).expect("read core stdout");
        assert!(count > 0, "kl-core closed stdout unexpectedly");
        serde_json::from_str(line.trim()).expect("core emitted invalid JSON")
    }

    fn shutdown(mut self) {
        self.stdin.take();
        let _ = self.child.wait();
    }
}

impl Drop for CoreProcess {
    fn drop(&mut self) {
        if self.child.try_wait().ok().flatten().is_none() {
            let _ = self.child.kill();
            let _ = self.child.wait();
        }
    }
}

fn event_type(value: &Value) -> Option<&str> {
    value.get("params")?.get("event")?.get("type")?.as_str()
}

fn handshake_params() -> Value {
    json!({
        "client_name": "session-resume-test",
        "client_version": "0.1.0",
        "protocol_versions": [1],
    })
}

#[test]
fn resumed_session_continues_after_core_restart() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    std::fs::write(
        temp.path().join("script.json"),
        r#"[
            {"tool_calls":[{"id":"append","name":"shell","arguments":{"command":"echo ran >> resumed.txt"}}]},
            {"text":"done"}
        ]"#,
    )
    .unwrap();
    let config_path = temp.path().join("kl.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
max_iterations = 4

[policy]
auto_approve_shell = true
"#,
    )
    .unwrap();
    let home = temp.path().join("home");

    let mut core = CoreProcess::start(&config_path, &home);
    let handshake = core.request("kl.handshake", handshake_params());
    assert!(handshake.get("error").is_none(), "{handshake}");
    let created = core.request(
        "session.create",
        json!({ "workspace": workspace.to_string_lossy() }),
    );
    assert!(created.get("error").is_none(), "{created}");
    let session_id = created["result"]["session_id"]
        .as_str()
        .expect("session id")
        .to_owned();
    core.shutdown();

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let resumed = core.request("session.resume", json!({ "session_id": session_id }));
    assert_eq!(resumed["result"]["message_count"], 0, "{resumed}");
    let started = core.request(
        "turn.start",
        json!({ "session_id": session_id, "prompt": "first turn" }),
    );
    assert!(started.get("error").is_none(), "{started}");
    let completed = core.wait_for_turn();
    assert_eq!(
        event_type(&completed),
        Some("turn.completed"),
        "{completed}"
    );
    assert_eq!(
        completed["params"]["event"]["data"]["status"], "completed",
        "{completed}"
    );
    assert_eq!(completed["params"]["event"]["data"]["text"], "done");
    core.shutdown();

    assert_eq!(
        std::fs::read_to_string(workspace.join("resumed.txt"))
            .unwrap()
            .lines()
            .count(),
        1
    );
    let session = home.join("sessions").join(&session_id);
    let meta = std::fs::read_to_string(session.join("session.json")).unwrap();
    assert!(meta.contains("\"message_count\": 4"), "{meta}");
    let meta_value: Value = serde_json::from_str(&meta).unwrap();
    assert_eq!(meta_value["status"], "completed");
    assert_eq!(meta_value["task"], "first turn");
    assert_eq!(meta_value["prompt"], "first turn");
    assert_eq!(meta_value["last_message"], "done");

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let resumed = core.request("session.resume", json!({ "session_id": session_id }));
    assert_eq!(resumed["result"]["message_count"], 4, "{resumed}");
    let started = core.request(
        "turn.start",
        json!({ "session_id": session_id, "prompt": "second turn" }),
    );
    assert!(started.get("error").is_none(), "{started}");
    let completed = core.wait_for_turn();
    assert_eq!(
        event_type(&completed),
        Some("turn.completed"),
        "{completed}"
    );
    assert_eq!(
        completed["params"]["event"]["data"]["status"], "completed",
        "{completed}"
    );
    core.shutdown();

    assert_eq!(
        std::fs::read_to_string(workspace.join("resumed.txt"))
            .unwrap()
            .lines()
            .count(),
        2
    );
    let meta = std::fs::read_to_string(session.join("session.json")).unwrap();
    assert!(meta.contains("\"message_count\": 8"), "{meta}");
    let meta_value: Value = serde_json::from_str(&meta).unwrap();
    assert_eq!(meta_value["status"], "completed");
    assert_eq!(meta_value["task"], "first turn");
    assert_eq!(meta_value["prompt"], "second turn");
    assert_eq!(meta_value["last_message"], "done");
    let state = std::fs::read_to_string(session.join("state.json")).unwrap();
    assert_eq!(state.matches("\"done\"").count(), 2, "{state}");
    let events = std::fs::read_to_string(session.join("events.jsonl")).unwrap();
    assert!(events.contains("\"turn.completed\""), "{events}");
    assert_eq!(
        std::fs::read_to_string(home.join("audit/events.jsonl"))
            .unwrap()
            .lines()
            .count(),
        2
    );
}
