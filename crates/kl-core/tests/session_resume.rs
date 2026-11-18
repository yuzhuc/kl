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
    let rollout = find_rollout(&home, &session_id);
    let rollout_text = std::fs::read_to_string(&rollout).unwrap();
    let meta_value = rollout_meta(&rollout_text);
    assert_eq!(meta_value["message_count"], 4, "{rollout_text}");
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
    let rollout_text = std::fs::read_to_string(&rollout).unwrap();
    let meta_value = rollout_meta(&rollout_text);
    assert_eq!(meta_value["message_count"], 8, "{rollout_text}");
    assert_eq!(meta_value["status"], "completed");
    assert_eq!(meta_value["task"], "first turn");
    assert_eq!(meta_value["prompt"], "second turn");
    assert_eq!(meta_value["last_message"], "done");
    let state = rollout_text
        .lines()
        .rev()
        .find(|line| line.contains("\"type\":\"state\""))
        .expect("state line");
    assert_eq!(state.matches("\"done\"").count(), 2, "{state}");
    assert!(
        rollout_text.contains("\"turn.completed\""),
        "{rollout_text}"
    );
    assert_eq!(
        std::fs::read_to_string(home.join("audit/events.jsonl"))
            .unwrap()
            .lines()
            .count(),
        2
    );
}

#[test]
fn history_replays_after_restart_without_tool_arguments() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let script_path = temp.path().join("script.json");
    std::fs::write(
        &script_path,
        r#"[
            {"tool_calls":[{"id":"append-1","name":"shell","arguments":{"command":"echo history-secret-token >> history.txt"}}]},
            {"text":"first answer"}
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
    let started = core.request(
        "turn.start",
        json!({ "session_id": session_id, "prompt": "first prompt" }),
    );
    assert!(started.get("error").is_none(), "{started}");
    core.wait_for_turn();
    core.shutdown();

    std::fs::write(
        &script_path,
        r#"[
            {"tool_calls":[{"id":"append-2","name":"shell","arguments":{"command":"echo history-secret-token >> history.txt"}}]},
            {"text":"second answer"}
        ]"#,
    )
    .unwrap();

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let resumed = core.request("session.resume", json!({ "session_id": session_id }));
    assert!(resumed.get("error").is_none(), "{resumed}");
    let started = core.request(
        "turn.start",
        json!({ "session_id": session_id, "prompt": "second prompt" }),
    );
    assert!(started.get("error").is_none(), "{started}");
    core.wait_for_turn();
    core.shutdown();

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let history = core.request(
        "session.history",
        json!({ "session_id": session_id, "limit": 20 }),
    );
    assert!(history.get("error").is_none(), "{history}");
    let messages = history["result"]["messages"]
        .as_array()
        .expect("history messages");
    let roles: Vec<&str> = messages
        .iter()
        .map(|entry| entry["role"].as_str().unwrap())
        .collect();
    assert_eq!(
        roles,
        vec!["user", "tool", "assistant", "user", "tool", "assistant"],
        "{history}"
    );
    let texts: Vec<&str> = messages
        .iter()
        .map(|entry| entry["text"].as_str().unwrap())
        .collect();
    assert_eq!(
        texts,
        vec![
            "first prompt",
            "",
            "first answer",
            "second prompt",
            "",
            "second answer"
        ],
        "{history}"
    );
    assert_eq!(messages[1]["tool"]["name"], "shell");
    assert_eq!(messages[1]["tool"]["ok"].as_bool(), Some(true));
    assert!(
        messages[1]["tool"]["summary"]
            .as_str()
            .unwrap()
            .contains("exit 0")
    );
    assert_eq!(messages[4]["tool"]["name"], "shell");
    assert!(messages[0]["timestamp"].is_string());
    assert!(messages[0]["seq"].as_u64().is_some());
    let seqs: Vec<u64> = messages
        .iter()
        .map(|entry| entry["seq"].as_u64().unwrap())
        .collect();
    assert!(
        seqs.windows(2).all(|window| window[0] < window[1]),
        "{seqs:?}"
    );

    let serialized = serde_json::to_string(&history["result"]).unwrap();
    for forbidden in ["args", "api_key", "history-secret-token"] {
        assert!(
            !serialized.contains(forbidden),
            "history leaked {forbidden}: {serialized}"
        );
    }

    let limited = core.request(
        "session.history",
        json!({ "session_id": session_id, "limit": 2 }),
    );
    let limited_messages = limited["result"]["messages"].as_array().unwrap();
    assert_eq!(limited_messages.len(), 2, "{limited}");
    assert_eq!(limited_messages[0]["role"], "tool");
    assert_eq!(limited_messages[1]["role"], "assistant");
    assert_eq!(limited_messages[1]["text"], "second answer");
    core.shutdown();

    assert_eq!(
        std::fs::read_to_string(workspace.join("history.txt"))
            .unwrap()
            .lines()
            .count(),
        2
    );
}

#[test]
fn empty_session_history_returns_empty_array() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    std::fs::write(temp.path().join("script.json"), r#"[{"text":"unused"}]"#).unwrap();
    let config_path = temp.path().join("kl.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
"#,
    )
    .unwrap();
    let home = temp.path().join("home");

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let created = core.request(
        "session.create",
        json!({ "workspace": workspace.to_string_lossy() }),
    );
    assert!(created.get("error").is_none(), "{created}");
    let session_id = created["result"]["session_id"]
        .as_str()
        .expect("session id")
        .to_owned();
    let history = core.request(
        "session.history",
        json!({ "session_id": session_id, "limit": 10 }),
    );
    assert_eq!(history["result"]["messages"], json!([]));
    core.shutdown();
}

fn find_rollout(home: &Path, id: &str) -> std::path::PathBuf {
    fn search(root: &Path, suffix: &str) -> Option<std::path::PathBuf> {
        for entry in std::fs::read_dir(root).ok()?.flatten() {
            let path = entry.path();
            if path.is_dir() {
                if let Some(found) = search(&path, suffix) {
                    return Some(found);
                }
            } else if path
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.starts_with("rollout-") && name.ends_with(suffix))
            {
                return Some(path);
            }
        }
        None
    }

    search(&home.join("sessions"), &format!("-{id}.jsonl")).expect("rollout file")
}

fn rollout_meta(rollout_text: &str) -> Value {
    let line = rollout_text.lines().next().expect("meta line");
    let value: Value = serde_json::from_str(line).expect("meta json");
    value["meta"].clone()
}

#[test]
fn archived_sessions_move_to_the_archived_store() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    std::fs::write(temp.path().join("script.json"), r#"[{"text":"unused"}]"#).unwrap();
    let config_path = temp.path().join("kl.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
"#,
    )
    .unwrap();
    let home = temp.path().join("home");

    let mut core = CoreProcess::start(&config_path, &home);
    core.request("kl.handshake", handshake_params());
    let created = core.request(
        "session.create",
        json!({ "workspace": workspace.to_string_lossy() }),
    );
    assert!(created["result"]["session_id"].is_string(), "{created}");
    let session_id = created["result"]["session_id"].as_str().unwrap().to_owned();

    let archived = core.request(
        "session.archive",
        json!({ "session_id": session_id, "archived": true }),
    );
    assert_eq!(archived["result"]["archived"], true, "{archived}");
    let listed = core.request("session.list", json!({}));
    assert_eq!(
        listed["result"]["sessions"][0]["archived"], true,
        "{listed}"
    );
    assert!(home.join("archived_sessions").read_dir().unwrap().count() > 0);

    let unarchived = core.request(
        "session.archive",
        json!({ "session_id": session_id, "archived": false }),
    );
    assert_eq!(unarchived["result"]["archived"], false, "{unarchived}");
    let listed = core.request("session.list", json!({}));
    assert_eq!(
        listed["result"]["sessions"][0]["archived"], false,
        "{listed}"
    );
    assert!(home.join("archived_sessions").read_dir().unwrap().count() == 0);
    core.shutdown();
}
