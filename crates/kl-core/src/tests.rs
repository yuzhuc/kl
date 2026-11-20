use super::*;
use crate::history::history_message_text;
use std::io::Cursor;
use std::sync::Barrier;

#[derive(Clone, Default)]
struct Buffer(Arc<Mutex<Vec<u8>>>);

impl Write for Buffer {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        self.0.lock().unwrap().extend_from_slice(bytes);
        Ok(bytes.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

struct BlockingApprovalProvider {
    first: Arc<Barrier>,
    release_first: Arc<Barrier>,
    second: Arc<Barrier>,
    release_second: Arc<Barrier>,
    calls: AtomicUsize,
}

impl Provider for BlockingApprovalProvider {
    fn id(&self) -> &str {
        "scripted"
    }

    fn kind(&self) -> &str {
        "scripted"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("scripted")]
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        _cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        if self.calls.fetch_add(1, Ordering::SeqCst) == 0 {
            self.first.wait();
            self.release_first.wait();
            Ok(ChatOutcome {
                text: String::new(),
                tool_calls: vec![kl_provider::ToolCall {
                    id: "call-1".to_owned(),
                    name: "write_file".to_owned(),
                    arguments: r#"{"path":"out.txt","content":"approved"}"#.to_owned(),
                }],
                finish_reason: FinishReason::ToolCalls,
                usage: Usage::default(),
            })
        } else {
            self.second.wait();
            self.release_second.wait();
            Ok(ChatOutcome {
                text: "done".to_owned(),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            })
        }
    }
}

fn serve(input: &str, config: Option<PathBuf>, home: PathBuf) -> Vec<Value> {
    let buffer = Buffer::default();
    run_with_io_args(
        Cursor::new(input),
        Emitter::new(Box::new(buffer.clone())),
        config,
        Some(home),
    )
    .unwrap();
    let bytes = buffer.0.lock().unwrap().clone();
    String::from_utf8(bytes)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

#[test]
fn approval_broker_timeout_and_cancel_deny() {
    let broker = ApprovalBroker::new(Duration::from_millis(20));
    let receiver = broker.register("timeout");
    let started = Instant::now();
    assert_eq!(
        broker.wait_registered("timeout", receiver, &AtomicBool::new(false)),
        ApprovalDecision::Deny
    );
    assert!(started.elapsed() >= Duration::from_millis(15));

    let receiver = broker.register("cancel");
    assert_eq!(
        broker.wait_registered("cancel", receiver, &AtomicBool::new(true)),
        ApprovalDecision::Deny
    );
}

#[test]
fn handshake_then_ping() {
    let home = tempfile::tempdir().unwrap();
    let output = serve(
        r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"kl.ping","params":{"nonce":"abc"}}
"#,
        None,
        home.path().to_path_buf(),
    );
    assert_eq!(output.len(), 2);
    assert_eq!(output[0]["result"]["protocol_version"], 1);
    assert_eq!(output[0]["result"]["server_name"], "kl-core");
    assert_eq!(output[1]["result"]["pong"], true);
    assert_eq!(output[1]["result"]["nonce"], "abc");
}

#[test]
fn rejects_incompatible_version() {
    let home = tempfile::tempdir().unwrap();
    let output = serve(
        r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[9]}}
"#,
        None,
        home.path().to_path_buf(),
    );
    assert_eq!(output.len(), 1);
    assert_eq!(output[0]["error"]["code"], error_codes::VERSION_MISMATCH);
    assert_eq!(output[0]["error"]["data"]["server_max"], 1);
}

#[test]
fn requires_handshake_for_sessions() {
    let home = tempfile::tempdir().unwrap();
    let output = serve(
        r#"{"jsonrpc":"2.0","id":1,"method":"session.list","params":{}}
"#,
        None,
        home.path().to_path_buf(),
    );
    assert_eq!(output[0]["error"]["code"], error_codes::NOT_INITIALIZED);
}

#[test]
fn routes_three_native_protocols_and_redacts_keys() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(temp.path().join("providers")).unwrap();
    std::fs::write(
        temp.path().join("providers/opencode.toml"),
        r#"
api_key = "super-secret-key"
[protocols]
openai-chat = "https://gateway.example/v1/chat/completions"
anthropic = "https://gateway.example/v1/messages"
openai-responses = "https://gateway.example/v1/responses"
[[models]]
id = "gpt-5-chat"
protocol = "openai-chat"
context_window = 128000
compact_threshold = 0.8
max_output_tokens = 4096
[[models]]
id = "claude-native"
protocol = "anthropic"
context_window = 200000
compact_threshold = 0.75
max_output_tokens = 8192
[[models]]
id = "gpt-5-responses"
protocol = "openai-responses"
context_window = 256000
compact_threshold = 0.9
max_output_tokens = 16384
url = "https://gateway.example/custom/responses"
"#,
    )
    .unwrap();
    std::fs::write(
        temp.path().join("kl.toml"),
        r#"
[agent]
provider = "opencode"
model = "gpt-5-responses"
[providers.opencode]
enabled = true
path = "providers/opencode.toml"
"#,
    )
    .unwrap();
    let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
    let input = format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"provider.list\",\"params\":{{}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\",\"provider\":\"opencode\",\"model\":\"gpt-5-responses\"}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"config.get\",\"params\":{{}}}}\n"
    );
    let output = serve(
        &input,
        Some(temp.path().join("kl.toml")),
        temp.path().join(".kl"),
    );
    let providers = &output.iter().find(|value| value["id"] == 2).unwrap()["result"];
    let models = providers["models"].as_array().unwrap();
    assert_eq!(models.len(), 3);
    assert_eq!(providers["active_provider"], "opencode");
    assert_eq!(providers["active_model"], "gpt-5-responses");
    let model = |id: &str| models.iter().find(|model| model["model"] == id).unwrap();
    assert_eq!(model("gpt-5-chat")["protocol"], "openai-chat");
    assert_eq!(model("claude-native")["protocol"], "anthropic");
    assert_eq!(model("gpt-5-responses")["protocol"], "openai-responses");
    assert_eq!(model("gpt-5-responses")["context_window"], 256000);
    let threshold = model("gpt-5-responses")["compact_threshold"]
        .as_f64()
        .unwrap();
    assert!((threshold - 0.9).abs() < 1e-6);
    assert_eq!(model("gpt-5-responses")["max_output_tokens"], 16384);
    assert_eq!(
        model("gpt-5-responses")["url"],
        "https://gateway.example/custom/responses"
    );

    let create = output.iter().find(|value| value["id"] == 3).unwrap();
    assert_eq!(create["result"]["provider"], "opencode");
    assert_eq!(create["result"]["model"], "gpt-5-responses");

    let config = output.iter().find(|value| value["id"] == 4).unwrap();
    assert!(!config.to_string().contains("super-secret-key"));
    assert!(config.to_string().contains("configured"));
}

#[test]
fn disabled_provider_has_no_models_and_selection_fails() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::write(
        temp.path().join("kl.toml"),
        r#"
[agent]
provider = "opencode"
model = "gpt-5-chat"
[providers.opencode]
enabled = false
path = "providers/missing.toml"
"#,
    )
    .unwrap();
    let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
    let input = format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"provider.list\",\"params\":{{}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\",\"provider\":\"opencode\"}}}}\n"
    );
    let output = serve(
        &input,
        Some(temp.path().join("kl.toml")),
        temp.path().join(".kl"),
    );
    let providers = output.iter().find(|value| value["id"] == 2).unwrap();
    assert_eq!(providers["result"]["models"].as_array().unwrap().len(), 0);
    let create = output.iter().find(|value| value["id"] == 3).unwrap();
    assert_eq!(create["error"]["code"], error_codes::PROVIDER_UNAVAILABLE);
    assert!(
        create["error"]["message"]
            .as_str()
            .unwrap()
            .contains("disabled")
    );
}

#[test]
fn provider_upsert_and_key_survive_core_restart() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = temp.path().join("kl.toml");
    let home_path = temp.path().join("home");
    let input = r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"provider.upsert","params":{"id":"local","protocols":{"openai-responses":"https://gateway.example/custom/responses?tenant=kl"},"models":[{"id":"gpt-5-responses","name":"Local GPT","protocol":"openai-responses","context_window":1000,"compact_threshold":0.8,"max_output_tokens":100}],"enabled":true}}
{"jsonrpc":"2.0","id":3,"method":"provider.set_api_key","params":{"id":"local","key":"super-secret"}}
{"jsonrpc":"2.0","id":4,"method":"config.set_default_model","params":{"provider":"local","model":"gpt-5-responses"}}
"#;
    let first = serve(input, Some(config_path.clone()), home_path.clone());
    assert!(
        first.iter().all(|value| value.get("error").is_none()),
        "{first:?}"
    );

    let second = serve(
        r#"{"jsonrpc":"2.0","id":1,"method":"kl.handshake","params":{"client_name":"test","client_version":"0.1.0","protocol_versions":[1]}}
{"jsonrpc":"2.0","id":2,"method":"provider.list","params":{}}
{"jsonrpc":"2.0","id":3,"method":"config.get","params":{}}
"#,
        Some(config_path),
        home_path.clone(),
    );
    let providers = second.iter().find(|value| value["id"] == 2).unwrap();
    let models = providers["result"]["models"].as_array().unwrap();
    assert_eq!(models.len(), 1);
    assert_eq!(models[0]["provider"], "local");
    assert_eq!(models[0]["model"], "gpt-5-responses");
    assert_eq!(models[0]["name"], "Local GPT");
    assert_eq!(models[0]["protocol"], "openai-responses");
    assert_eq!(models[0]["enabled"], true);
    assert_eq!(models[0]["key_configured"], true);
    assert_eq!(
        models[0]["url"],
        "https://gateway.example/custom/responses?tenant=kl"
    );
    let config = second.iter().find(|value| value["id"] == 3).unwrap();
    assert!(!config.to_string().contains("super-secret"));
    assert_eq!(
        std::fs::read_to_string(home_path.join("credentials/local.key")).unwrap(),
        "super-secret"
    );
    assert!(
        !std::fs::read_to_string(temp.path().join("providers/local.toml"))
            .unwrap()
            .contains("super-secret")
    );
}
#[test]
fn creates_scripted_session_and_lists_it() {
    let temp = tempfile::tempdir().unwrap();
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
    let workspace = temp.path().to_string_lossy().replace('\\', "\\\\");
    let input = format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session.create\",\"params\":{{\"workspace\":\"{workspace}\"}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session.list\",\"params\":{{}}}}\n"
    );
    let output = serve(
        &input,
        Some(temp.path().join("kl.toml")),
        temp.path().join(".kl"),
    );
    let create = output
        .iter()
        .find(|value| value["id"] == 2)
        .expect("session.create response");
    let list = output
        .iter()
        .find(|value| value["id"] == 3)
        .expect("session.list response");
    assert_eq!(create["result"]["provider"], "scripted", "{create}");
    assert!(create["result"]["session_id"].as_str().is_some());
    assert_eq!(list["result"]["sessions"].as_array().unwrap().len(), 1);
}

#[test]
fn turn_state_tracks_working_approval_and_completion() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;

    let (_, metadata) = core.router.get(Some("scripted"), Some("scripted")).unwrap();
    let first = Arc::new(Barrier::new(2));
    let release_first = Arc::new(Barrier::new(2));
    let second = Arc::new(Barrier::new(2));
    let release_second = Arc::new(Barrier::new(2));
    core.router.register(
        metadata,
        Arc::new(BlockingApprovalProvider {
            first: first.clone(),
            release_first: release_first.clone(),
            second: second.clone(),
            release_second: release_second.clone(),
            calls: AtomicUsize::new(0),
        }),
    );

    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    assert_eq!(created["turn_state"], TURN_STATE_IDLE);
    let listed = core.session_list().unwrap();
    assert_eq!(listed["sessions"][0]["turn_state"], TURN_STATE_IDLE);

    core.turn_start(json!({"session_id": session_id, "prompt": "write a file"}))
        .unwrap();
    first.wait();
    assert_eq!(
        session_turn_state(&core, &session_id).as_deref(),
        Some(TURN_STATE_WORKING)
    );
    release_first.wait();

    wait_for_session_turn_state(&core, &session_id, TURN_STATE_WAITING_APPROVAL);
    let approval_id = wait_for_approval_id(&buffer);
    let response = core
        .approval_respond(json!({
            "session_id": session_id,
            "approval_id": approval_id,
            "decision": "allow"
        }))
        .unwrap();
    assert_eq!(response["accepted"], true);

    wait_for_session_turn_state(&core, &session_id, TURN_STATE_WORKING);
    second.wait();
    assert_eq!(
        session_turn_state(&core, &session_id).as_deref(),
        Some(TURN_STATE_WORKING)
    );
    release_second.wait();

    wait_for_threads(&mut core);
    assert_eq!(
        session_turn_state(&core, &session_id).as_deref(),
        Some(TURN_STATE_IDLE)
    );
    let listed = core.session_list().unwrap();
    assert_eq!(listed["sessions"][0]["turn_state"], TURN_STATE_IDLE);
}

#[test]
fn turn_state_survives_core_restart() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let session_id = {
        let (mut core, _) = new_test_core(config.clone(), home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        let session_id = created["session_id"].as_str().unwrap().to_owned();
        core.store
            .set_turn_state(&session_id, TURN_STATE_WAITING_APPROVAL)
            .unwrap();
        session_id
    };

    let (mut restarted, _) = new_test_core(config, home.path());
    restarted.handshaken = true;
    let listed = restarted.session_list().unwrap();
    assert_eq!(
        listed["sessions"][0]["turn_state"],
        TURN_STATE_WAITING_APPROVAL
    );
    let resumed = restarted
        .session_resume(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(resumed["turn_state"], TURN_STATE_WAITING_APPROVAL);
}

use kl_provider::{ChatOutcome, FinishReason, ModelInfo, ProviderErrorKind};

fn copy_dir(source: &Path, target: &Path) {
    std::fs::create_dir_all(target).unwrap();
    for entry in std::fs::read_dir(source).unwrap() {
        let entry = entry.unwrap();
        if entry.file_name() == ".git" {
            continue;
        }
        let destination = target.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&entry.path(), &destination);
        } else {
            std::fs::copy(entry.path(), destination).unwrap();
        }
    }
}

fn test_git(workspace: &Path, args: &[&str]) {
    let status = std::process::Command::new("git")
        .arg("-C")
        .arg(workspace)
        .args([
            "-c",
            "user.email=review@example.test",
            "-c",
            "user.name=review",
        ])
        .args(args)
        .status()
        .unwrap();
    assert!(status.success(), "git {:?} failed", args);
}

fn output_lines(buffer: &Buffer) -> Vec<Value> {
    let bytes = buffer.0.lock().unwrap().clone();
    String::from_utf8(bytes)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

fn wait_for_threads(core: &mut Core) {
    let deadline = Instant::now() + Duration::from_secs(15);
    while Instant::now() < deadline && core.threads.iter().any(|handle| !handle.is_finished()) {
        std::thread::sleep(Duration::from_millis(25));
    }
    assert!(
        core.threads.iter().all(|handle| handle.is_finished()),
        "core turn thread did not finish"
    );
    for handle in core.threads.drain(..) {
        handle.join().unwrap();
    }
}

fn session_turn_state(core: &Core, session_id: &str) -> Option<String> {
    core.store
        .load(session_id)
        .ok()
        .map(|data| data.meta.turn_state)
}

fn wait_for_session_turn_state(core: &Core, session_id: &str, expected: &str) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        if session_turn_state(core, session_id).as_deref() == Some(expected) {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "turn state did not become {expected}: {:?}",
            session_turn_state(core, session_id)
        );
        std::thread::sleep(Duration::from_millis(10));
    }
}

fn wait_for_approval_id(buffer: &Buffer) -> String {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        for value in output_lines(buffer) {
            if value["params"]["event"]["type"] == "tool.approval.requested" {
                if let Some(id) = value["params"]["event"]["data"]["approval_id"].as_str() {
                    return id.to_owned();
                }
            }
        }
        assert!(
            Instant::now() < deadline,
            "tool approval request was not emitted"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
}

fn deep_config(temp: &Path) -> PathBuf {
    std::fs::write(
            temp.join("script.json"),
            r#"[
                {"text":"explored src/lib.rs:1"},
                {"text":"PLAN write out.txt"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"out.txt","content":"done"}}]},
                {"text":"IMPLEMENTATION wrote out.txt:1"},
                {"text":"REVIEW out.txt:1 evidence=done"},
                {"text":"VERIFY out.txt:1 tests passed"}
            ]"#,
        )
        .unwrap();
    let config = temp.join("kl.toml");
    std::fs::write(
        &config,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
scripted_sequential = true
quality = "deep"

[policy]
auto_approve_edits = true
"#,
    )
    .unwrap();
    config
}

fn new_test_core(config: PathBuf, home: &Path) -> (Core, Buffer) {
    let buffer = Buffer::default();
    let core = Core::new(
        ServerContext::resolve(Some(config), Some(home.to_path_buf())),
        Emitter::new(Box::new(buffer.clone())),
    )
    .unwrap();
    (core, buffer)
}

fn command_config(temp: &Path) -> PathBuf {
    std::fs::write(temp.join("script.json"), r#"[{"text":"unused"}]"#).unwrap();
    let config = temp.join("kl.toml");
    std::fs::write(
        &config,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
max_iterations = 2
"#,
    )
    .unwrap();
    config
}

#[test]
fn session_status_reports_the_loaded_session_and_turn_state() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    let status = core.session_status(json!({})).unwrap();
    assert_eq!(status["session_id"], session_id);
    let expected_workspace = std::fs::canonicalize(&workspace).unwrap();
    let expected_text = expected_workspace.to_string_lossy();
    let expected_text = expected_text
        .strip_prefix(r"\\?\")
        .unwrap_or(expected_text.as_ref());
    assert_eq!(status["workspace"], expected_text);
    assert_eq!(status["provider"], "scripted");
    assert_eq!(status["model"], "scripted");
    assert_eq!(status["context_window"], 8192);
    assert_eq!(status["turn_active"], false);
    assert_eq!(status["approval_mode"], "ask");
    assert!(status["sandbox"]["backend"].as_str().is_some());
    assert!(status["sandbox"]["capabilities"].is_object());

    {
        let mut sessions = core.sessions.lock().unwrap();
        sessions.get_mut(&session_id).unwrap().active_turn = Some("turn-test".to_owned());
    }
    let active = core
        .session_status(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(active["turn_active"], true);
}

#[test]
fn permissions_modes_are_hot_updated_and_persisted() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config_path.clone(), home.path());
    core.handshaken = true;

    for mode in ["ask", "plan", "acceptEdits", "dontAsk", "auto", "read-only"] {
        let result = core
            .permissions_set(json!({"approval_mode": mode}))
            .unwrap();
        assert_eq!(result["approval_mode"], mode);
        assert_eq!(core.permissions_get().unwrap(), result);
        let saved = Config::load(&config_path).unwrap();
        assert_eq!(saved.agent.approval_mode.as_str(), mode);
        let (edits, shell, plugins) = match mode {
            "acceptEdits" => (true, false, false),
            "dontAsk" | "auto" => (true, true, true),
            _ => (false, false, false),
        };
        assert_eq!(saved.policy.auto_approve_edits, edits);
        assert_eq!(saved.policy.auto_approve_shell, shell);
        assert_eq!(saved.policy.auto_approve_plugins, plugins);
        assert_eq!(
            core.tool_access(),
            if matches!(mode, "plan" | "read-only") {
                ToolAccess::ReadOnly
            } else {
                ToolAccess::Full
            }
        );
    }

    let error = core
        .permissions_set(json!({"approval_mode": "unknown"}))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);
    assert_eq!(
        core.permissions_get().unwrap()["approval_mode"],
        "read-only"
    );
}

#[test]
fn auto_preserves_policy_flags_while_dont_ask_overrides_them() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::write(temp.path().join("script.json"), r#"[{"text":"unused"}]"#).unwrap();
    let config_path = temp.path().join("kl.toml");
    std::fs::write(
        &config_path,
        r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"

[policy]
auto_approve_edits = true
auto_approve_shell = false
auto_approve_plugins = true
"#,
    )
    .unwrap();
    let home = tempfile::tempdir().unwrap();

    {
        let (mut core, _) = new_test_core(config_path.clone(), home.path());
        core.handshaken = true;
        core.permissions_set(json!({"approval_mode": "auto"}))
            .unwrap();
        assert!(core.config.policy.auto_approve_edits);
        assert!(!core.config.policy.auto_approve_shell);
        assert!(core.config.policy.auto_approve_plugins);
        let effective = core.effective_policy_config();
        assert!(effective.auto_approve_edits);
        assert!(!effective.auto_approve_shell);
        assert!(effective.auto_approve_plugins);
    }

    {
        let (mut restarted, _) = new_test_core(config_path.clone(), home.path());
        restarted.handshaken = true;
        assert!(restarted.config.policy.auto_approve_edits);
        assert!(!restarted.config.policy.auto_approve_shell);
        assert!(restarted.config.policy.auto_approve_plugins);
        restarted
            .permissions_set(json!({"approval_mode": "dontAsk"}))
            .unwrap();
        assert!(restarted.config.policy.auto_approve_edits);
        assert!(restarted.config.policy.auto_approve_shell);
        assert!(restarted.config.policy.auto_approve_plugins);
    }
}

#[test]
fn permissions_and_reasoning_effort_survive_restart() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    {
        let (mut core, _) = new_test_core(config_path.clone(), home.path());
        core.handshaken = true;
        assert_eq!(
            core.agent_config_get().unwrap()["reasoning_effort"],
            "default"
        );
        core.permissions_set(json!({"approval_mode": "auto"}))
            .unwrap();
        core.agent_config_set(json!({
            "reasoning_effort": "high",
            "web_search": false,
            "network_access": false,
            "output_verbosity": "concise",
            "reasoning_summary": "detailed",
            "memory_enabled": false,
            "plugins_enabled": false,
            "custom_instructions": "  be terse  ",
            "personal_style": "professional",
            "warmth": "low",
            "enthusiasm": "high",
            "emoji_usage": "less",
            "visual_answers": "off",
            "heading_style": "detailed",
            "quick_answers": false
        }))
        .unwrap();
    }

    let (mut restarted, _) = new_test_core(config_path, home.path());
    restarted.handshaken = true;
    assert_eq!(
        restarted.permissions_get().unwrap()["approval_mode"],
        "auto"
    );
    let agent = restarted.agent_config_get().unwrap();
    assert_eq!(agent["reasoning_effort"], "high");
    assert_eq!(agent["web_search"], false);
    assert_eq!(agent["network_access"], false);
    assert_eq!(agent["output_verbosity"], "concise");
    assert_eq!(agent["reasoning_summary"], "detailed");
    assert_eq!(agent["memory_enabled"], false);
    assert_eq!(agent["plugins_enabled"], false);
    assert_eq!(agent["custom_instructions"], "be terse");
    assert_eq!(agent["personal_style"], "professional");
    assert_eq!(agent["warmth"], "low");
    assert_eq!(agent["enthusiasm"], "high");
    assert_eq!(agent["emoji_usage"], "less");
    assert_eq!(agent["visual_answers"], "off");
    assert_eq!(agent["heading_style"], "detailed");
    assert_eq!(agent["quick_answers"], false);
    assert!(restarted.tools.get("web_fetch").is_none());
    assert!(restarted.tools.get("memory").is_none());
    assert!(restarted.effective_policy_config().deny_network);
    let error = restarted
        .agent_config_set(json!({"reasoning_effort": "extreme"}))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);
}

#[test]
fn personalization_block_reflects_agent_settings() {
    let temp = tempfile::tempdir().unwrap();
    let config_path = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config_path, home.path());
    core.handshaken = true;
    core.agent_config_set(json!({
        "personal_style": "friendly",
        "warmth": "high",
        "enthusiasm": "low",
        "emoji_usage": "more",
        "visual_answers": "on",
        "heading_style": "concise",
        "quick_answers": false
    }))
    .unwrap();

    let block = core.personalization_block();
    assert!(block.contains("friendly"));
    assert!(block.contains("warm and supportive"));
    assert!(block.contains("enthusiasm understated"));
    assert!(block.contains("emoji"));
    assert!(block.contains("visual structure"));
    assert!(block.contains("headings and lists short"));
    assert!(!block.contains("general knowledge is sufficient"));
}

#[test]
fn session_compact_reuses_agent_compaction_and_keeps_tool_pairs() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    let empty = core
        .session_compact(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(empty["omitted_messages"], 0);
    assert!(empty["estimated_tokens"].as_u64().unwrap() > 0);
    let tool_call = kl_provider::ToolCall {
        id: "latest-call".to_owned(),
        name: "read_file".to_owned(),
        arguments: r#"{"path":"README.md"}"#.to_owned(),
    };
    let mut messages = Vec::new();
    for round in 0..3 {
        messages.push(ChatMessage::user(format!("round {round}")));
        if round == 2 {
            messages.push(ChatMessage::assistant("", vec![tool_call.clone()]));
            messages.push(ChatMessage::tool("latest-call", "file contents"));
            messages.push(ChatMessage::assistant("latest answer", Vec::new()));
        } else {
            messages.push(ChatMessage::assistant(
                format!("answer {round}"),
                Vec::new(),
            ));
        }
    }
    {
        let mut sessions = core.sessions.lock().unwrap();
        sessions.get_mut(&session_id).unwrap().messages = messages;
    }

    let compacted = core
        .session_compact(json!({"session_id": session_id}))
        .unwrap();
    assert!(compacted["omitted_messages"].as_u64().unwrap() <= 3);
    assert!(compacted["estimated_tokens"].as_u64().unwrap() > 0);
    assert_eq!(compacted["context_window"], 8192);
    assert!((compacted["compact_threshold"].as_f64().unwrap() - 0.8).abs() < 0.001);

    let history = {
        let sessions = core.sessions.lock().unwrap();
        sessions.get(&session_id).unwrap().messages.clone()
    };
    let tool_ids: Vec<String> = history
        .iter()
        .filter_map(|message| message.tool_call_id.clone())
        .collect();
    for call in history.iter().flat_map(|message| &message.tool_calls) {
        assert!(
            tool_ids.contains(&call.id),
            "tool call {} lost its result",
            call.id
        );
    }
    assert!(
        history
            .iter()
            .any(|message| message.text() == "latest answer")
    );
    let persisted = core.store.load(&session_id).unwrap();
    assert_eq!(persisted.messages.len(), history.len());
    let events = core.store.read_events(&session_id).unwrap();
    assert!(events.iter().any(|event| matches!(
        &event.event,
        CoreEvent::ContextCompacted {
            trigger,
            summary_used: false,
            ..
        } if trigger == "session.compact"
    )));

    {
        let mut sessions = core.sessions.lock().unwrap();
        sessions.get_mut(&session_id).unwrap().active_turn = Some("turn-test".to_owned());
    }
    let active_error = core
        .session_compact(json!({"session_id": session_id}))
        .unwrap_err();
    assert_eq!(active_error.code, error_codes::TURN_IN_PROGRESS);

    let empty_home = tempfile::tempdir().unwrap();
    let (mut empty, _) = new_test_core(temp.path().join("empty.toml"), empty_home.path());
    empty.handshaken = true;
    let error = empty.session_compact(json!({})).unwrap_err();
    assert_eq!(error.code, error_codes::SESSION_NOT_FOUND);
}

#[test]
fn install_metadata_keeps_a_stable_installation_id() {
    let temp = tempfile::tempdir().unwrap();
    write_install_metadata(temp.path());
    let first = std::fs::read_to_string(temp.path().join("installation_id")).unwrap();
    assert_eq!(first.len(), 36);

    let version: Value =
        serde_json::from_str(&std::fs::read_to_string(temp.path().join("version.json")).unwrap())
            .unwrap();
    assert_eq!(version["version"], CORE_VERSION);
    assert_eq!(version["protocol"], PROTOCOL_VERSION);
    assert_eq!(version["event_schema"], EVENT_SCHEMA_VERSION);

    write_install_metadata(temp.path());
    assert_eq!(
        std::fs::read_to_string(temp.path().join("installation_id")).unwrap(),
        first
    );
}

#[test]
fn workspace_init_creates_without_overwriting_and_rejects_escape() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    let nested = workspace.join("nested");
    let outside = temp.path().join("outside");
    std::fs::create_dir_all(&nested).unwrap();
    std::fs::create_dir_all(&outside).unwrap();
    let config = command_config(temp.path());
    let config_path = config.clone();
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    core.session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();

    let created = core
        .workspace_init(json!({"path": "nested", "force": false}))
        .unwrap();
    assert_eq!(created["created"], true);
    let agents_path = PathBuf::from(created["path"].as_str().unwrap());
    assert!(agents_path.is_file());
    std::fs::write(&agents_path, "keep me").unwrap();

    let existing = core
        .workspace_init(json!({"path": "nested", "force": false}))
        .unwrap();
    assert_eq!(existing["created"], false);
    assert_eq!(std::fs::read_to_string(&agents_path).unwrap(), "keep me");

    let forced = core
        .workspace_init(json!({"path": "nested", "force": true}))
        .unwrap();
    assert_eq!(forced["created"], true);
    assert!(
        std::fs::read_to_string(&agents_path)
            .unwrap()
            .starts_with("# AGENTS.md")
    );

    let error = core
        .workspace_init(json!({"path": outside.to_string_lossy()}))
        .unwrap_err();
    assert_eq!(error.code, error_codes::POLICY_DENIED);
    assert!(!outside.join("AGENTS.md").exists());

    let standalone_home = tempfile::tempdir().unwrap();
    let (mut standalone, _) = new_test_core(config_path, standalone_home.path());
    standalone.handshaken = true;
    let initialized = standalone
        .workspace_init(json!({"path": workspace.to_string_lossy()}))
        .unwrap();
    assert_eq!(initialized["created"], true);
    assert!(workspace.join("AGENTS.md").is_file());
}

#[cfg(unix)]
#[test]
fn workspace_init_rejects_agents_symlink() {
    use std::os::unix::fs::symlink;

    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let outside = temp.path().join("outside.md");
    std::fs::write(&outside, "outside").unwrap();
    symlink(&outside, workspace.join("AGENTS.md")).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    core.session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();

    let error = core.workspace_init(json!({"force": true})).unwrap_err();
    assert_eq!(error.code, error_codes::POLICY_DENIED);
    assert_eq!(std::fs::read_to_string(outside).unwrap(), "outside");
}

#[test]
fn review_run_finds_fixture_rules_without_a_provider() {
    let temp = tempfile::tempdir().unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
    let workspace = temp.path().join("repo");
    copy_dir(&root.join("tests/fixtures/review-repo/base"), &workspace);
    test_git(&workspace, &["init"]);
    test_git(&workspace, &["add", "."]);
    test_git(&workspace, &["commit", "-m", "base"]);
    copy_dir(&root.join("tests/fixtures/review-repo/changed"), &workspace);
    let home = tempfile::tempdir().unwrap();
    let escaped = workspace.to_string_lossy().replace('\\', "\\\\");
    let input = format!(
        "{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"kl.handshake\",\"params\":{{\"client_name\":\"test\",\"client_version\":\"0.1.0\",\"protocol_versions\":[1]}}}}\n\
             {{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"review.run\",\"params\":{{\"workspace\":\"{escaped}\",\"mode\":\"strict\"}}}}\n"
    );
    let output = serve(&input, None, home.path().to_path_buf());
    let review = output.iter().find(|value| value["id"] == 2).unwrap();
    let rules: Vec<&str> = review["result"]["findings"]
        .as_array()
        .unwrap()
        .iter()
        .map(|finding| finding["rule"].as_str().unwrap())
        .collect();
    for expected in [
        "debug-unwrap",
        "secret-openai",
        "missing-test",
        "cross-file-command-injection",
        "llm-review-unavailable",
    ] {
        assert!(
            rules.contains(&expected),
            "missing {expected}; got {rules:?}"
        );
    }
}

#[test]
fn rewind_from_a_user_message_truncates_and_hides_the_old_branch() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    core.turn_start(json!({"session_id": session_id, "prompt": "first"}))
        .unwrap();
    wait_for_threads(&mut core);
    let first_user_seq = core
        .store
        .read_events(&session_id)
        .unwrap()
        .iter()
        .find_map(|stored| match &stored.event {
            CoreEvent::UserMessage { text, .. } if text == "first" => Some(stored.seq),
            _ => None,
        })
        .expect("first user event");

    core.turn_start(json!({"session_id": session_id, "prompt": "second"}))
        .unwrap();
    wait_for_threads(&mut core);

    let started = core
        .turn_start(json!({
            "session_id": session_id,
            "prompt": "again",
            "rewind_from_seq": first_user_seq,
        }))
        .unwrap();
    assert!(started["turn_id"].as_str().is_some());
    wait_for_threads(&mut core);

    let history = core
        .session_history(json!({"session_id": session_id}))
        .unwrap();
    let entries = history["messages"].as_array().unwrap();
    assert_eq!(entries.len(), 2, "{history}");
    assert_eq!(entries[0]["role"], "user");
    assert_eq!(entries[0]["text"], "again");
    assert_eq!(entries[1]["role"], "assistant");

    let events = core.store.read_events(&session_id).unwrap();
    assert!(events.iter().any(|stored| matches!(
        &stored.event,
        CoreEvent::ConversationRewound { from_seq } if *from_seq == first_user_seq
    )));
    // The old branch stays on disk for audit; the marker hides it.
    assert!(events.iter().any(|stored| matches!(
        &stored.event,
        CoreEvent::UserMessage { text, .. } if text == "second"
    )));

    let audit = core.audit_tail(json!({ "limit": 20 })).unwrap();
    let expected = first_user_seq.to_string();
    assert!(audit["records"].as_array().unwrap().iter().any(|record| {
        record["tool"].as_str() == Some("session")
            && record["action"].as_str() == Some("rewind")
            && record["resource"].as_str() == Some(expected.as_str())
    }));
}

#[test]
fn rewind_rejects_events_that_are_not_live_user_messages() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    wait_for_threads(&mut core);

    let events = core.store.read_events(&session_id).unwrap();
    let started_seq = events
        .iter()
        .find(|stored| matches!(stored.event, CoreEvent::TurnStarted { .. }))
        .map(|stored| stored.seq)
        .expect("turn.started event");

    let not_user = core
        .turn_start(json!({
            "session_id": session_id,
            "prompt": "rewrite",
            "rewind_from_seq": started_seq,
        }))
        .unwrap_err();
    assert_eq!(not_user.code, error_codes::INVALID_PARAMS);

    let missing = core
        .turn_start(json!({
            "session_id": session_id,
            "prompt": "rewrite",
            "rewind_from_seq": 9999,
        }))
        .unwrap_err();
    assert_eq!(missing.code, error_codes::INVALID_PARAMS);
}

#[test]
fn rewind_validation_failure_writes_no_audit() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    wait_for_threads(&mut core);

    let events = core.store.read_events(&session_id).unwrap();
    let started_seq = events
        .iter()
        .find(|stored| matches!(stored.event, CoreEvent::TurnStarted { .. }))
        .map(|stored| stored.seq)
        .expect("turn.started event");
    let error = core
        .turn_start(json!({
            "session_id": session_id,
            "prompt": "rewrite",
            "rewind_from_seq": started_seq,
        }))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);

    let audit = core.audit_tail(json!({ "limit": 20 })).unwrap();
    assert!(
        !audit["records"]
            .as_array()
            .unwrap()
            .iter()
            .any(|record| record["action"].as_str() == Some("rewind"))
    );
}

#[test]
fn live_events_hide_rewound_intervals_but_keep_markers() {
    let user = |seq: u64, text: &str| StoredSessionEvent {
        seq,
        at: format!("t{seq}"),
        event: CoreEvent::UserMessage {
            turn_id: format!("turn-{seq}"),
            text: text.to_owned(),
        },
    };
    let marker = |seq: u64, from_seq: u64| StoredSessionEvent {
        seq,
        at: format!("t{seq}"),
        event: CoreEvent::ConversationRewound { from_seq },
    };
    let events = vec![
        user(1, "old"),
        marker(2, 1),
        user(3, "dropped"),
        marker(4, 3),
        user(5, "kept"),
    ];
    let live: Vec<u64> = live_events(events).iter().map(|event| event.seq).collect();
    assert_eq!(live, vec![2, 4, 5]);
}

#[test]
fn build_session_history_pairs_retained_user_messages_with_newest_events() {
    let message = |role: &str, text: &str| json!({"role": role, "content": text});
    let messages = vec![
        message("user", "kept question"),
        message("assistant", "answer"),
    ];
    let events = vec![
        StoredSessionEvent {
            seq: 1,
            at: "t1".to_owned(),
            event: CoreEvent::UserMessage {
                turn_id: "old".to_owned(),
                text: "compacted question".to_owned(),
            },
        },
        StoredSessionEvent {
            seq: 2,
            at: "t2".to_owned(),
            event: CoreEvent::TurnDelta {
                turn_id: "old".to_owned(),
                text: "old answer".to_owned(),
            },
        },
        StoredSessionEvent {
            seq: 3,
            at: "t3".to_owned(),
            event: CoreEvent::UserMessage {
                turn_id: "new".to_owned(),
                text: "kept question".to_owned(),
            },
        },
        StoredSessionEvent {
            seq: 4,
            at: "t4".to_owned(),
            event: CoreEvent::TurnDelta {
                turn_id: "new".to_owned(),
                text: "answer".to_owned(),
            },
        },
    ];
    let entries = build_session_history(&messages, &events);
    let user = entries
        .iter()
        .find(|entry| entry.role == "user")
        .expect("user entry");
    assert_eq!(user.seq, Some(3));
    assert_eq!(user.text, "kept question");
}

#[test]
fn turn_start_records_one_user_message_before_the_agent_starts() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    wait_for_threads(&mut core);

    let events = core.store.read_events(&session_id).unwrap();
    let user_messages: Vec<_> = events
        .iter()
        .filter_map(|stored| match &stored.event {
            CoreEvent::UserMessage { turn_id, text } => Some((stored.seq, turn_id, text)),
            _ => None,
        })
        .collect();
    assert_eq!(user_messages.len(), 1);
    assert_eq!(user_messages[0].2, "hello");
    let started_seq = events
        .iter()
        .find(|stored| {
            matches!(
                &stored.event,
                CoreEvent::TurnStarted { turn_id } if turn_id == user_messages[0].1
            )
        })
        .map(|stored| stored.seq)
        .expect("matching turn.started event");
    assert!(user_messages[0].0 < started_seq);
}

#[test]
fn turn_start_loads_session_from_disk() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let session_id = {
        let (mut core, _) = new_test_core(config.clone(), home.path());
        core.handshaken = true;
        let created = core
            .session_create(json!({"workspace": workspace.to_string_lossy()}))
            .unwrap();
        created["session_id"].as_str().unwrap().to_owned()
    };

    let (mut restarted, _) = new_test_core(config, home.path());
    restarted.handshaken = true;
    assert!(
        !restarted
            .sessions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .contains_key(&session_id),
        "session should only exist on disk after restart"
    );
    let missing = restarted
        .turn_start(json!({"session_id": "missing-session", "prompt": "hello"}))
        .unwrap_err();
    assert_eq!(missing.code, error_codes::SESSION_NOT_FOUND);

    let started = restarted
        .turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    assert_eq!(started["provider"], "scripted");
    assert_eq!(started["model"], "scripted");
    assert!(started["turn_id"].as_str().is_some_and(|id| !id.is_empty()));
    wait_for_threads(&mut restarted);

    let events = restarted.store.read_events(&session_id).unwrap();
    assert!(
        events.iter().any(|stored| matches!(
            &stored.event,
            CoreEvent::UserMessage { text, .. } if text == "hello"
        )),
        "turn.start should load the session from disk and record the user message"
    );
}

#[test]
fn session_history_uses_raw_user_text_reasoning_and_tool_details() {
    let messages = vec![
        json!({
            "role": "user",
            "content": [{"type": "text", "data": "skills\nOriginal prompt"}]
        }),
        json!({
            "role": "assistant",
            "content": [],
            "tool_calls": [{"id": "call-1", "name": "read_file", "arguments": "{}"}]
        }),
        json!({
            "role": "tool",
            "tool_call_id": "call-1",
            "content": [{"type": "text", "data": "file text"}]
        }),
        json!({
            "role": "assistant",
            "content": [{"type": "text", "data": "done"}]
        }),
    ];
    let event = |seq, event| StoredSessionEvent {
        seq,
        at: format!("2026-10-26T00:00:{seq:02}.000Z"),
        event,
    };
    let events = vec![
        event(
            1,
            CoreEvent::UserMessage {
                turn_id: "turn-1".to_owned(),
                text: "Original prompt".to_owned(),
            },
        ),
        event(
            2,
            CoreEvent::TurnStarted {
                turn_id: "turn-1".to_owned(),
            },
        ),
        event(
            3,
            CoreEvent::TurnReasoning {
                turn_id: "turn-1".to_owned(),
                text: "Check ".to_owned(),
            },
        ),
        event(
            4,
            CoreEvent::TurnReasoning {
                turn_id: "turn-1".to_owned(),
                text: "files.".to_owned(),
            },
        ),
        event(
            5,
            CoreEvent::ToolStarted {
                turn_id: "turn-1".to_owned(),
                call_id: "call-1".to_owned(),
                name: "read_file".to_owned(),
                args: json!({"path": "a.txt"}),
            },
        ),
        event(
            6,
            CoreEvent::ToolCompleted {
                turn_id: "turn-1".to_owned(),
                call_id: "call-1".to_owned(),
                name: "read_file".to_owned(),
                ok: true,
                summary: "read a file".to_owned(),
                output: Some("file text".to_owned()),
                diff: Some("+new".to_owned()),
            },
        ),
        event(
            7,
            CoreEvent::TurnDelta {
                turn_id: "turn-1".to_owned(),
                text: "done".to_owned(),
            },
        ),
        event(
            8,
            CoreEvent::TurnCompleted {
                turn_id: "turn-1".to_owned(),
                status: "completed".to_owned(),
                text: "done".to_owned(),
                input_tokens: 1,
                output_tokens: 1,
                cost_usd: None,
            },
        ),
    ];

    let history = build_session_history(&messages, &events);
    assert_eq!(history.len(), 3);
    assert_eq!(history[0].role, "user");
    assert_eq!(history[0].text, "Original prompt");
    assert_eq!(history[0].seq, Some(1));
    assert_eq!(history[1].role, "tool");
    assert_eq!(history[1].seq, Some(6));
    let tool = history[1].tool.as_ref().unwrap();
    assert_eq!(tool.output.as_deref(), Some("file text"));
    assert_eq!(tool.diff.as_deref(), Some("+new"));
    assert_eq!(history[2].role, "assistant");
    assert_eq!(history[2].seq, Some(7));
    assert_eq!(history[2].reasoning.as_deref(), Some("Check files."));
}

#[test]
fn history_pairs_the_retained_turn_after_compaction() {
    let messages = vec![
        json!({"role":"user","content":[{"type":"text","data":"kept prompt"}]}),
        json!({"role":"assistant","content":[{"type":"text","data":"kept answer"}]}),
    ];
    let event = |seq, event| StoredSessionEvent {
        seq,
        at: format!("2026-10-26T00:00:{seq:02}.000Z"),
        event,
    };
    let events = vec![
        event(
            1,
            CoreEvent::UserMessage {
                turn_id: "dropped".to_owned(),
                text: "dropped prompt".to_owned(),
            },
        ),
        event(
            2,
            CoreEvent::TurnDelta {
                turn_id: "dropped".to_owned(),
                text: "dropped answer".to_owned(),
            },
        ),
        event(
            3,
            CoreEvent::UserMessage {
                turn_id: "kept".to_owned(),
                text: "kept prompt".to_owned(),
            },
        ),
        event(
            4,
            CoreEvent::TurnDelta {
                turn_id: "kept".to_owned(),
                text: "kept answer".to_owned(),
            },
        ),
    ];

    let history = build_session_history(&messages, &events);

    assert_eq!(history.len(), 2);
    assert_eq!(history[0].seq, Some(3));
    assert_eq!(history[0].text, "kept prompt");
    assert_eq!(history[1].seq, Some(4));
    assert_eq!(history[1].text, "kept answer");
}

#[test]
fn history_pairs_the_retained_tool_after_compaction() {
    let messages = vec![
        json!({"role":"user","content":[{"type":"text","data":"kept prompt"}]}),
        json!({
            "role": "assistant",
            "content": [],
            "tool_calls": [{"id": "kept-call", "name": "read_file", "arguments": "{}"}]
        }),
        json!({
            "role": "tool",
            "tool_call_id": "kept-call",
            "content": [{"type":"text","data":"file text"}]
        }),
        json!({"role":"assistant","content":[{"type":"text","data":"kept answer"}]}),
    ];
    let event = |seq, event| StoredSessionEvent {
        seq,
        at: format!("2026-10-26T00:00:{seq:02}.000Z"),
        event,
    };
    let events = vec![
        event(
            1,
            CoreEvent::UserMessage {
                turn_id: "dropped".to_owned(),
                text: "dropped prompt".to_owned(),
            },
        ),
        event(
            2,
            CoreEvent::ToolCompleted {
                turn_id: "dropped".to_owned(),
                call_id: "dropped-call".to_owned(),
                name: "read_file".to_owned(),
                ok: true,
                summary: "dropped tool".to_owned(),
                output: None,
                diff: None,
            },
        ),
        event(
            3,
            CoreEvent::UserMessage {
                turn_id: "kept".to_owned(),
                text: "kept prompt".to_owned(),
            },
        ),
        event(
            4,
            CoreEvent::TurnDelta {
                turn_id: "kept".to_owned(),
                text: String::new(),
            },
        ),
        event(
            5,
            CoreEvent::ToolCompleted {
                turn_id: "kept".to_owned(),
                call_id: "kept-call".to_owned(),
                name: "read_file".to_owned(),
                ok: true,
                summary: "kept tool".to_owned(),
                output: Some("file text".to_owned()),
                diff: None,
            },
        ),
        event(
            6,
            CoreEvent::TurnDelta {
                turn_id: "kept".to_owned(),
                text: "kept answer".to_owned(),
            },
        ),
    ];

    let history = build_session_history(&messages, &events);

    assert_eq!(history.len(), 3);
    assert_eq!(history[0].seq, Some(3));
    assert_eq!(history[1].role, "tool");
    assert_eq!(history[1].seq, Some(5));
    assert_eq!(history[1].tool.as_ref().unwrap().summary, "kept tool");
    assert_eq!(history[2].seq, Some(6));
    assert_eq!(history[2].text, "kept answer");
}

#[test]
fn memory_methods_round_trip_and_reject_unsafe_input() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    let empty = core.memory_list(json!({"session_id": session_id})).unwrap();
    assert_eq!(empty["entries"].as_array().unwrap().len(), 0);

    let written = core
        .memory_write(json!({
            "session_id": session_id,
            "name": "notes.md",
            "content": "# hello"
        }))
        .unwrap();
    assert_eq!(written["name"], "notes.md");
    assert_eq!(written["scope"], "workspace");
    assert!(workspace.join(".kl/memory/notes.md").is_file());

    let listed = core.memory_list(json!({"session_id": session_id})).unwrap();
    assert_eq!(listed["entries"].as_array().unwrap().len(), 1);
    assert_eq!(listed["entries"][0]["name"], "notes.md");

    let read = core
        .memory_read(json!({"session_id": session_id, "name": "notes.md"}))
        .unwrap();
    assert_eq!(read["content"], "# hello");
    assert_eq!(read["scope"], "workspace");

    for name in ["../escape.md", "sub/dir.md", "..", "notes.txt"] {
        let error = core
            .memory_write(json!({
                "session_id": session_id,
                "name": name,
                "content": "x"
            }))
            .unwrap_err();
        assert_eq!(error.code, error_codes::INVALID_PARAMS, "{name}");
    }
    assert!(!workspace.join(".kl/escape.md").exists());

    let error = core
        .memory_write(json!({
            "session_id": session_id,
            "name": "big.md",
            "content": "a".repeat(70 * 1024)
        }))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);

    let deleted = core
        .memory_delete(json!({"session_id": session_id, "name": "notes.md"}))
        .unwrap();
    assert_eq!(deleted["deleted"], true);
    assert!(!workspace.join(".kl/memory/notes.md").exists());

    let listed = core.memory_list(json!({"session_id": session_id})).unwrap();
    assert_eq!(listed["entries"].as_array().unwrap().len(), 0);
}

#[test]
fn memory_is_injected_into_the_system_prompt_and_dropped_after_delete() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(workspace.join(".kl/memory")).unwrap();
    std::fs::write(workspace.join(".kl/memory/notes.md"), "remember the milk").unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;

    let system = Arc::new(Mutex::new(Vec::new()));
    let (_, metadata) = core.router.get(Some("scripted"), Some("scripted")).unwrap();
    core.router.register(
        metadata,
        Arc::new(CapturingProvider {
            system: system.clone(),
        }),
    );

    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    wait_for_threads(&mut core);
    let captured = system
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .clone();
    assert!(
        !captured.is_empty(),
        "the agent must have called the provider"
    );
    assert!(
        captured[0].contains("remember the milk"),
        "memory must reach the system prompt: {}",
        captured[0]
    );

    let records = core.audit.tail(50).unwrap();
    assert!(
        records
            .iter()
            .any(|record| record["tool"] == "memory" && record["action"] == "inject"),
        "the injection must be recorded in the audit trail"
    );

    core.memory_delete(json!({"session_id": session_id, "name": "notes.md"}))
        .unwrap();
    core.turn_start(json!({"session_id": session_id, "prompt": "hello again"}))
        .unwrap();
    wait_for_threads(&mut core);
    let captured = system
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .clone();
    let last = captured.last().unwrap();
    assert!(
        !last.contains("remember the milk"),
        "a deleted memory must not be injected again: {last}"
    );
}

#[test]
fn user_memory_is_injected_but_the_workspace_wins() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(workspace.join(".kl/memory")).unwrap();
    let home = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(home.path().join("memory")).unwrap();
    std::fs::write(home.path().join("memory/global.md"), "global note").unwrap();
    std::fs::write(home.path().join("memory/dup.md"), "user version").unwrap();
    std::fs::write(workspace.join(".kl/memory/dup.md"), "workspace version").unwrap();

    let config = command_config(temp.path());
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;

    let system = Arc::new(Mutex::new(Vec::new()));
    let (_, metadata) = core.router.get(Some("scripted"), Some("scripted")).unwrap();
    core.router.register(
        metadata,
        Arc::new(CapturingProvider {
            system: system.clone(),
        }),
    );

    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap();
    wait_for_threads(&mut core);

    let captured = system
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .clone();
    let prompt = captured.first().unwrap();
    assert!(prompt.contains("global note"), "{prompt}");
    assert!(prompt.contains("workspace version"), "{prompt}");
    assert!(!prompt.contains("user version"), "{prompt}");
}

#[test]
fn agent_preset_defaults_and_tool_access_are_bounded() {
    assert_eq!(
        capped_tool_access(ToolAccess::Full, AgentPreset::Plan.tool_access()),
        ToolAccess::ReadOnly
    );
    assert_eq!(
        capped_tool_access(ToolAccess::Full, AgentPreset::Verify.tool_access()),
        ToolAccess::VerifyOnly
    );
    assert_eq!(
        capped_tool_access(ToolAccess::ReadOnly, AgentPreset::Build.tool_access()),
        ToolAccess::ReadOnly
    );
    assert_eq!(
        capped_tool_access(ToolAccess::ReadOnly, AgentPreset::Deep.tool_access()),
        ToolAccess::ReadOnly
    );
}

#[test]
fn agent_list_includes_custom_profiles() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(home.path().join("agents")).unwrap();
    std::fs::write(
            home.path().join("agents/reviewer.toml"),
            "id = \"reviewer\"\nname = \"Reviewer\"\ndescription = \"strict\"\npolicy = \"read-only\"\n",
        )
        .unwrap();
    let (core, _) = new_test_core(config, home.path());
    let list = core.agent_list().unwrap();
    let agents = list["agents"].as_array().unwrap();
    assert!(agents.iter().any(|agent| agent["id"] == "reviewer"
        && agent["policy"] == "read-only"
        && agent["built_in"] == false));
    assert!(
        agents
            .iter()
            .any(|agent| agent["id"] == "deep" && agent["built_in"] == true)
    );
}

#[test]
fn turn_start_rejects_unknown_agent_preset() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    let error = core
        .turn_start(json!({
            "session_id": "missing",
            "prompt": "hello",
            "agent": "turbo"
        }))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);
    assert!(error.message.contains("unknown agent preset"));
}

#[test]
fn turn_start_prepends_the_agent_preset_prompt() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "hello",
        "agent": "plan"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let first = {
        let sessions = core.sessions.lock().unwrap();
        sessions[&session_id].messages[0].text()
    };
    assert!(first.contains(AgentPreset::Plan.prompt_prefix()));
    assert!(first.ends_with("hello"));
    let history = core
        .session_history(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(history["messages"][0]["text"], "hello");
}

#[test]
fn turn_start_keeps_skill_summaries_out_of_the_user_message() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let skill = home.path().join("skills").join("alpha");
    std::fs::create_dir_all(&skill).unwrap();
    std::fs::write(
        skill.join("SKILL.md"),
        "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
    )
    .unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "hello"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let first = {
        let sessions = core.sessions.lock().unwrap();
        sessions[&session_id].messages[0].text()
    };
    assert_eq!(first, "hello");
    assert!(!first.contains("SECRET BODY"));
    let history = core
        .session_history(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(history["messages"][0]["text"], "hello");
}

#[test]
fn skill_rpc_lists_summaries_and_loads_body() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let skill = workspace.join(".kl/skills/alpha");
    std::fs::create_dir_all(&skill).unwrap();
    std::fs::write(
        skill.join("SKILL.md"),
        "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
    )
    .unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    let list = core.skill_list(json!({"session_id": session_id})).unwrap();
    assert_eq!(list["skills"][0]["name"], "alpha");
    assert_eq!(list["skills"][0]["description"], "Alpha description");
    assert!(!list.to_string().contains("SECRET BODY"));
    assert!(!list.to_string().contains("SKILL.md"));

    let loaded = core
        .skill_get(json!({"session_id": session_id, "name": "alpha"}))
        .unwrap();
    assert_eq!(loaded["name"], "alpha");
    assert_eq!(loaded["content"], "SECRET BODY\n");

    let missing = core
        .skill_get(json!({"session_id": session_id, "name": "missing"}))
        .unwrap_err();
    assert_eq!(missing.code, error_codes::INVALID_PARAMS);
    assert!(missing.message.contains("unknown skill: missing"));
}

#[test]
fn skill_rpc_lists_by_workspace_and_rejects_missing_target() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    let skill = workspace.join(".kl/skills/alpha");
    std::fs::create_dir_all(&skill).unwrap();
    std::fs::write(
        skill.join("SKILL.md"),
        "---\nname: alpha\ndescription: Alpha description\n---\nSECRET BODY\n",
    )
    .unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;

    let list = core
        .skill_list(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    assert_eq!(list["skills"][0]["name"], "alpha");
    assert_eq!(list["skills"][0]["description"], "Alpha description");

    let missing = core.skill_list(json!({})).unwrap_err();
    assert_eq!(missing.code, error_codes::INVALID_PARAMS);
    assert!(missing.message.contains("session_id or workspace"));
}

#[test]
fn skill_rpc_loads_home_skill_body() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let skill = home.path().join("skills/global");
    std::fs::create_dir_all(&skill).unwrap();
    std::fs::write(
        skill.join("SKILL.md"),
        "---\nname: global\ndescription: Global skill\n---\nGLOBAL BODY\n",
    )
    .unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();

    let list = core.skill_list(json!({"session_id": session_id})).unwrap();
    assert_eq!(list["skills"][0]["name"], "global");

    let loaded = core
        .skill_get(json!({"session_id": session_id, "name": "global"}))
        .unwrap();
    assert_eq!(loaded["name"], "global");
    assert_eq!(loaded["content"], "GLOBAL BODY\n");
}

#[test]
fn mcp_list_is_empty_without_mcp_tools() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (core, _) = new_test_core(config, home.path());
    let result = core.mcp_list().unwrap();
    assert_eq!(result["tools"], json!([]));
}

#[test]
fn deep_mode_runs_five_stages_and_writes_the_workspace() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    let workspace = temp.path().to_string_lossy().to_string();
    let created = core
        .session_create(json!({"workspace": workspace}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "write done to out.txt",
        "quality": "deep"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let output = output_lines(&buffer);
    let completed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.completed")
        .expect("turn.completed event");
    assert_eq!(
        completed["params"]["event"]["data"]["status"], "completed",
        "{completed}"
    );
    let text = completed["params"]["event"]["data"]["text"]
        .as_str()
        .unwrap();
    assert!(text.contains("explorer -> planner -> implementer -> reviewer -> verifier"));
    assert!(text.contains("REVIEW out.txt:1"));
    assert!(text.contains("VERIFY out.txt:1"));
    assert_eq!(
        std::fs::read_to_string(temp.path().join("out.txt")).unwrap(),
        "done"
    );
}

#[test]
fn deep_prompts_include_index_context_only_for_explorer_and_planner() {
    let context = "3 files, 4 symbols.";
    let explorer = DeepStage::Explorer.prompt("task", Some(context));
    assert!(explorer.contains(context));
    assert!(explorer.contains("Repository index"));
    let planner = DeepStage::Planner.prompt("task", Some(context));
    assert!(planner.contains(context));
    let reviewer = DeepStage::Reviewer.prompt("task", Some(context));
    assert!(!reviewer.contains(context));
    let verifier = DeepStage::Verifier.prompt("task", Some(context));
    assert!(!verifier.contains(context));
}

struct SlowProvider;

/// Captures the system prompt of every request so tests can assert what
/// the agent was told.
struct CapturingProvider {
    system: Arc<Mutex<Vec<String>>>,
}

impl Provider for CapturingProvider {
    fn id(&self) -> &str {
        "capturing"
    }

    fn kind(&self) -> &str {
        "test"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("capturing")]
    }

    fn available(&self) -> (bool, String) {
        (true, "captures the system prompt".to_owned())
    }

    fn stream_chat(
        &self,
        request: &ChatRequest,
        _cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        self.system
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .push(request.system.clone());
        Ok(ChatOutcome {
            text: "ok".to_owned(),
            tool_calls: Vec::new(),
            finish_reason: FinishReason::Stop,
            usage: Usage::default(),
        })
    }
}

impl Provider for SlowProvider {
    fn id(&self) -> &str {
        "slow"
    }

    fn kind(&self) -> &str {
        "test"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("slow")]
    }

    fn available(&self) -> (bool, String) {
        (true, "slow provider".to_owned())
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        for _ in 0..50 {
            if cancel.is_cancelled() {
                return Err(ProviderError::cancelled());
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        Ok(ChatOutcome {
            text: "slow".to_owned(),
            tool_calls: Vec::new(),
            finish_reason: FinishReason::Stop,
            usage: Usage::default(),
        })
    }
}

#[test]
fn deep_mode_cancels_without_waiting_for_all_stages() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    core.router = ProviderRouter::new(
        vec![(
            ModelMetadata::new(
                "slow",
                "slow",
                Protocol::Scripted,
                "test://slow",
                8192,
                0.8,
                4096,
            ),
            Arc::new(SlowProvider) as Arc<dyn Provider>,
        )],
        "slow",
        "slow",
    );
    core.config.agent.provider = "slow".to_owned();
    core.config.agent.model = "slow".to_owned();
    let workspace = temp.path().to_string_lossy().to_string();
    let created = core
        .session_create(json!({"workspace": workspace}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    let started = core
        .turn_start(json!({
            "session_id": session_id,
            "prompt": "cancel this deep turn",
            "quality": "deep"
        }))
        .unwrap();
    let turn_id = started["turn_id"].as_str().unwrap().to_owned();
    core.turn_cancel(json!({
        "session_id": session_id,
        "turn_id": turn_id
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let output = output_lines(&buffer);
    let completed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.completed")
        .expect("turn.completed event");
    assert_eq!(
        completed["params"]["event"]["data"]["status"], "cancelled",
        "{completed}"
    );
}
struct FailAfterProvider {
    calls: AtomicUsize,
}

impl Provider for FailAfterProvider {
    fn id(&self) -> &str {
        "fail-after"
    }

    fn kind(&self) -> &str {
        "test"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("fail-after")]
    }

    fn available(&self) -> (bool, String) {
        (true, "fails after two calls".to_owned())
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        _cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let call = self.calls.fetch_add(1, Ordering::Relaxed);
        if call < 2 {
            return Ok(ChatOutcome {
                text: format!("stage {call}"),
                tool_calls: Vec::new(),
                finish_reason: FinishReason::Stop,
                usage: Usage::default(),
            });
        }
        Err(ProviderError::new(ProviderErrorKind::Server, "boom"))
    }
}

#[test]
fn deep_mode_reports_implementer_provider_failure() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    core.router = ProviderRouter::new(
        vec![(
            ModelMetadata::new(
                "fail-after",
                "fail-after",
                Protocol::Scripted,
                "test://fail-after",
                8192,
                0.8,
                4096,
            ),
            Arc::new(FailAfterProvider {
                calls: AtomicUsize::new(0),
            }) as Arc<dyn Provider>,
        )],
        "fail-after",
        "fail-after",
    );
    core.config.agent.provider = "fail-after".to_owned();
    core.config.agent.model = "fail-after".to_owned();
    let workspace = temp.path().to_string_lossy().to_string();
    let created = core
        .session_create(json!({"workspace": workspace}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "fail during implementation",
        "quality": "deep"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let output = output_lines(&buffer);
    let failed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.failed")
        .expect("turn.failed event");
    let message = failed["params"]["event"]["data"]["message"]
        .as_str()
        .unwrap();
    assert!(message.contains("boom"), "{message}");
}
struct ReviewMutationProvider {
    workspace: PathBuf,
    calls: AtomicUsize,
}

impl Provider for ReviewMutationProvider {
    fn id(&self) -> &str {
        "mutating-review"
    }

    fn kind(&self) -> &str {
        "test"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("mutating-review")]
    }

    fn available(&self) -> (bool, String) {
        (true, "writes during review".to_owned())
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        _cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        let call = self.calls.fetch_add(1, Ordering::Relaxed);
        if call == 3 {
            std::fs::write(self.workspace.join("sneaky.txt"), "extra").unwrap();
        }
        Ok(ChatOutcome {
            text: format!("stage {call}"),
            tool_calls: Vec::new(),
            finish_reason: FinishReason::Stop,
            usage: Usage::default(),
        })
    }
}

fn deep_turn_output(config: PathBuf, workspace: &Path) -> Vec<Value> {
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "deep stage test",
        "quality": "deep"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    output_lines(&buffer)
}

#[test]
fn deep_mode_reviewer_and_verifier_tools_are_hard_read_only() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    std::fs::write(
            temp.path().join("script.json"),
            r#"[
                {"text":"explored"},
                {"text":"plan"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"out.txt","content":"done"}}]},
                {"text":"implemented"},
                {"tool_calls":[{"name":"write_file","arguments":{"path":"sneaky.txt","content":"extra"}}]},
                {"text":"reviewed"},
                {"tool_calls":[{"name":"shell","arguments":{"command":"echo hacked > sneaky.txt","cwd":"."}}]},
                {"text":"verified"}
            ]"#,
        )
        .unwrap();
    let output = deep_turn_output(config, temp.path());
    let completed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.completed")
        .expect("turn.completed event");
    assert_eq!(completed["params"]["event"]["data"]["status"], "completed");
    assert!(!temp.path().join("sneaky.txt").exists());
    assert!(temp.path().join("out.txt").exists());
    let tool_events: Vec<&Value> = output
        .iter()
        .filter(|value| value["params"]["event"]["type"] == "tool.completed")
        .collect();
    assert!(tool_events.iter().any(|value| {
        value["params"]["event"]["data"]["name"] == "write_file"
            && value["params"]["event"]["data"]["ok"] == false
            && value["params"]["event"]["data"]["summary"]
                .as_str()
                .is_some_and(|text| text.contains("review-only"))
    }));
    assert!(tool_events.iter().any(|value| {
        value["params"]["event"]["data"]["name"] == "shell"
            && value["params"]["event"]["data"]["ok"] == false
            && value["params"]["event"]["data"]["summary"]
                .as_str()
                .is_some_and(|text| text.contains("verification stage allows only"))
    }));
}

#[test]
fn deep_mode_fingerprint_catches_out_of_band_reviewer_mutation() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    core.router = ProviderRouter::new(
        vec![(
            ModelMetadata::new(
                "mutating-review",
                "mutating-review",
                Protocol::Scripted,
                "test://mutating-review",
                8192,
                0.8,
                4096,
            ),
            Arc::new(ReviewMutationProvider {
                workspace: temp.path().to_path_buf(),
                calls: AtomicUsize::new(0),
            }) as Arc<dyn Provider>,
        )],
        "mutating-review",
        "mutating-review",
    );
    core.config.agent.provider = "mutating-review".to_owned();
    core.config.agent.model = "mutating-review".to_owned();
    let workspace = temp.path().to_string_lossy().to_string();
    let created = core
        .session_create(json!({"workspace": workspace}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({
        "session_id": session_id,
        "prompt": "detect mutation",
        "quality": "deep"
    }))
    .unwrap();
    wait_for_threads(&mut core);
    let output = output_lines(&buffer);
    let failed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.failed")
        .expect("turn.failed event");
    let message = failed["params"]["event"]["data"]["message"]
        .as_str()
        .unwrap();
    assert!(message.contains("modified the workspace"), "{message}");
    assert!(temp.path().join("sneaky.txt").exists());
}

#[test]
fn deep_mode_budgets_provider_without_usage() {
    let temp = tempfile::tempdir().unwrap();
    let config = deep_config(temp.path());
    let text = std::fs::read_to_string(&config).unwrap();
    std::fs::write(
        &config,
        text.replace(
            "quality = \"deep\"",
            "quality = \"deep\"\ndeep_token_budget = 1",
        ),
    )
    .unwrap();
    let output = deep_turn_output(config, temp.path());
    let failed = output
        .iter()
        .find(|value| value["params"]["event"]["type"] == "turn.failed")
        .expect("turn.failed event");
    let message = failed["params"]["event"]["data"]["message"]
        .as_str()
        .unwrap();
    assert!(message.contains("token budget"), "{message}");
}

struct BlockingReviewProvider {
    started: Arc<AtomicBool>,
}

impl Provider for BlockingReviewProvider {
    fn id(&self) -> &str {
        "blocking-review"
    }

    fn kind(&self) -> &str {
        "test"
    }

    fn models(&self) -> Vec<ModelInfo> {
        vec![ModelInfo::new("blocking-review")]
    }

    fn available(&self) -> (bool, String) {
        (true, "blocks until cancelled".to_owned())
    }

    fn stream_chat(
        &self,
        _request: &ChatRequest,
        cancel: &CancellationToken,
        _on_event: &mut dyn FnMut(ChatEvent),
    ) -> Result<ChatOutcome, ProviderError> {
        self.started.store(true, Ordering::Relaxed);
        while !cancel.is_cancelled() {
            std::thread::sleep(Duration::from_millis(10));
        }
        Err(ProviderError::cancelled())
    }
}

#[test]
fn review_chat_cancellation_exits_promptly() {
    let started = Arc::new(AtomicBool::new(false));
    let cancel = CancellationToken::new();
    let request = ChatRequest::new("blocking-review", vec![ChatMessage::user("review")]);
    let thread_cancel = cancel.clone();
    let thread_started = started.clone();
    let handle = std::thread::spawn(move || {
        stream_review_chat(
            Arc::new(BlockingReviewProvider {
                started: thread_started,
            }),
            request,
            thread_cancel,
            Duration::from_secs(5),
        )
    });
    let deadline = Instant::now() + Duration::from_secs(2);
    while !started.load(Ordering::Relaxed) && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }
    assert!(started.load(Ordering::Relaxed));
    let cancelled_at = Instant::now();
    cancel.cancel();
    let result = handle.join().unwrap();
    assert!(result.unwrap_err().contains("cancelled"));
    assert!(cancelled_at.elapsed() < Duration::from_secs(1));
}

#[test]
fn review_chat_timeout_exits_promptly() {
    let started = Arc::new(AtomicBool::new(false));
    let started_for_worker = started.clone();
    let started_at = Instant::now();
    let result = stream_review_chat(
        Arc::new(BlockingReviewProvider {
            started: started_for_worker,
        }),
        ChatRequest::new("blocking-review", vec![ChatMessage::user("review")]),
        CancellationToken::new(),
        Duration::from_millis(100),
    );
    assert!(result.unwrap_err().contains("timed out"));
    assert!(started_at.elapsed() < Duration::from_secs(1));
    assert!(started.load(Ordering::Relaxed));
}
#[test]
fn review_run_timeout_returns_warning_instead_of_blocking() {
    let temp = tempfile::tempdir().unwrap();
    std::fs::write(temp.path().join("a.txt"), "old\n").unwrap();
    test_git(temp.path(), &["init"]);
    test_git(temp.path(), &["add", "."]);
    test_git(temp.path(), &["commit", "-m", "base"]);
    std::fs::write(temp.path().join("a.txt"), "new\n").unwrap();
    let config = temp.path().join("kl.toml");
    std::fs::write(&config, "[agent]\nreview_timeout_secs = 1\n").unwrap();
    let home = tempfile::tempdir().unwrap();
    let (mut core, _buffer) = new_test_core(config, home.path());
    core.handshaken = true;
    core.config.agent.review_timeout_secs = 1;
    core.router = ProviderRouter::new(
        vec![(
            ModelMetadata::new(
                "blocking-review",
                "blocking-review",
                Protocol::Scripted,
                "test://blocking-review",
                8192,
                0.8,
                4096,
            ),
            Arc::new(BlockingReviewProvider {
                started: Arc::new(AtomicBool::new(false)),
            }) as Arc<dyn Provider>,
        )],
        "blocking-review",
        "blocking-review",
    );
    let started = Instant::now();
    let result = core
        .review_run(json!({
            "workspace": temp.path().to_string_lossy(),
            "mode": "strict"
        }))
        .unwrap();
    assert!(started.elapsed() < Duration::from_secs(3));
    let findings = result["findings"].as_array().unwrap();
    assert!(findings.iter().any(|finding| {
        finding["rule"] == "llm-review-unavailable"
            && finding["evidence"]
                .as_str()
                .is_some_and(|text| text.contains("timed out"))
    }));
}

#[test]
fn concurrent_emit_keeps_sequence_and_file_order() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    let before = core.store.last_seq(&session_id).unwrap();
    let hub = core.hub.clone();

    let mut handles = Vec::new();
    for thread in 0..8 {
        let hub = hub.clone();
        let session_id = session_id.clone();
        handles.push(std::thread::spawn(move || {
            for index in 0..50 {
                hub.emit(
                    &session_id,
                    CoreEvent::TurnDelta {
                        turn_id: "concurrent".to_owned(),
                        text: format!("{thread}-{index}"),
                    },
                )
                .unwrap();
            }
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }

    let events = core.store.read_events(&session_id).unwrap();
    let seqs: Vec<u64> = events.iter().map(|event| event.seq).collect();
    assert_eq!(seqs, (1..=before + 400).collect::<Vec<_>>());
}

#[test]
fn rewind_truncate_keeps_imported_leading_messages() {
    let plan = RewindPlan {
        from_seq: 7,
        user_events: vec![7],
    };
    let messages = vec![
        ChatMessage::user("old question"),
        ChatMessage::assistant("old answer", Vec::new()),
        ChatMessage::user("new question"),
        ChatMessage::assistant("new answer", Vec::new()),
    ];

    let (kept, cut) = plan.truncate(&messages).unwrap();

    assert_eq!(cut, 2);
    assert_eq!(kept.len(), 2);
    assert_eq!(kept[0].text(), "old question");
    assert_eq!(kept[1].text(), "old answer");
}

#[test]
fn rewind_truncate_still_rejects_compacted_targets() {
    let messages = vec![ChatMessage::user("kept question")];
    let plan = RewindPlan {
        from_seq: 1,
        user_events: vec![1, 3],
    };
    assert!(plan.truncate(&messages).is_err());

    let plan = RewindPlan {
        from_seq: 3,
        user_events: vec![1, 3],
    };
    let (kept, cut) = plan.truncate(&messages).unwrap();
    assert_eq!(cut, 0);
    assert!(kept.is_empty());
}

#[test]
fn history_pairs_imported_rows_without_consuming_events() {
    let messages = vec![
        json!({"role":"user","content":[{"type":"text","data":"imported question"}]}),
        json!({"role":"assistant","content":[{"type":"text","data":"imported answer"}]}),
        json!({"role":"user","content":[{"type":"text","data":"live question"}]}),
        json!({"role":"assistant","content":[{"type":"text","data":"live answer"}]}),
    ];
    let event = |seq, event| StoredSessionEvent {
        seq,
        at: format!("2026-10-26T00:00:{seq:02}.000Z"),
        event,
    };
    let events = vec![
        event(
            1,
            CoreEvent::UserMessage {
                turn_id: "live".to_owned(),
                text: "live question".to_owned(),
            },
        ),
        event(
            2,
            CoreEvent::TurnDelta {
                turn_id: "live".to_owned(),
                text: "live answer".to_owned(),
            },
        ),
    ];

    let history = build_session_history(&messages, &events);

    assert_eq!(history.len(), 4);
    assert_eq!(history[0].text, "imported question");
    assert_eq!(history[0].seq, None);
    assert_eq!(history[1].text, "imported answer");
    assert_eq!(history[1].seq, None);
    assert_eq!(history[2].seq, Some(1));
    assert_eq!(history[3].seq, Some(2));
    assert_eq!(history[3].text, "live answer");
}

#[test]
fn fork_then_rewind_keeps_inherited_messages() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    let source = created["session_id"].as_str().unwrap().to_owned();
    core.turn_start(json!({"session_id": source, "prompt": "first"}))
        .unwrap();
    wait_for_threads(&mut core);

    let fork = core.session_fork(json!({"session_id": source})).unwrap()["session_id"]
        .as_str()
        .unwrap()
        .to_owned();
    core.turn_start(json!({"session_id": fork, "prompt": "again"}))
        .unwrap();
    wait_for_threads(&mut core);
    let fork_turn_seq = core
        .store
        .read_events(&fork)
        .unwrap()
        .iter()
        .find_map(|stored| match &stored.event {
            CoreEvent::UserMessage { text, .. } if text == "again" => Some(stored.seq),
            _ => None,
        })
        .expect("fork user event");

    core.turn_start(json!({
        "session_id": fork,
        "prompt": "rewrite",
        "rewind_from_seq": fork_turn_seq,
    }))
    .unwrap();
    wait_for_threads(&mut core);

    let texts: Vec<String> = core
        .store
        .load(&fork)
        .unwrap()
        .messages
        .iter()
        .map(history_message_text)
        .collect();
    assert!(texts.iter().any(|text| text == "first"), "{texts:?}");
    assert!(texts.iter().any(|text| text == "rewrite"), "{texts:?}");
    assert!(!texts.iter().any(|text| text == "again"), "{texts:?}");

    let history = core.session_history(json!({"session_id": fork})).unwrap();
    let entries = history["messages"].as_array().unwrap();
    assert_eq!(entries[0]["text"], "first");
    assert_eq!(entries[0]["seq"], Value::Null);
}

#[test]
fn imported_messages_survive_rewind() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let mut data = core
        .store
        .create(
            &temp.path().to_string_lossy(),
            "scripted",
            "scripted",
            "medium",
        )
        .unwrap();
    data.messages = vec![
        json!({"role":"user","content":[{"type":"text","data":"imported question"}]}),
        json!({"role":"assistant","content":[{"type":"text","data":"imported answer"}]}),
    ];
    core.store.save(&data).unwrap();
    let id = data.meta.id.clone();

    core.turn_start(json!({"session_id": id, "prompt": "live"}))
        .unwrap();
    wait_for_threads(&mut core);
    let live_seq = core
        .store
        .read_events(&id)
        .unwrap()
        .iter()
        .find_map(|stored| match &stored.event {
            CoreEvent::UserMessage { text, .. } if text == "live" => Some(stored.seq),
            _ => None,
        })
        .expect("live user event");

    core.turn_start(json!({
        "session_id": id,
        "prompt": "rewrite",
        "rewind_from_seq": live_seq,
    }))
    .unwrap();
    wait_for_threads(&mut core);

    let texts: Vec<String> = core
        .store
        .load(&id)
        .unwrap()
        .messages
        .iter()
        .map(history_message_text)
        .collect();
    assert!(
        texts.iter().any(|text| text == "imported question"),
        "{texts:?}"
    );
    assert!(
        texts.iter().any(|text| text == "imported answer"),
        "{texts:?}"
    );
    assert!(texts.iter().any(|text| text == "rewrite"), "{texts:?}");
    assert!(!texts.iter().any(|text| text == "live"), "{texts:?}");
}

#[test]
fn turn_start_failure_clears_active_turn() {
    let temp = tempfile::tempdir().unwrap();
    let workspace = temp.path().join("workspace");
    std::fs::create_dir_all(&workspace).unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let created = core
        .session_create(json!({"workspace": workspace.to_string_lossy()}))
        .unwrap();
    let session_id = created["session_id"].as_str().unwrap().to_owned();
    std::fs::remove_dir_all(&workspace).unwrap();

    let error = core
        .turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap_err();
    assert_eq!(error.code, error_codes::INVALID_PARAMS);
    let status = core
        .session_status(json!({"session_id": session_id}))
        .unwrap();
    assert_eq!(status["turn_active"], false);

    let again = core
        .turn_start(json!({"session_id": session_id, "prompt": "hello"}))
        .unwrap_err();
    assert_eq!(again.code, error_codes::INVALID_PARAMS);
}

#[test]
fn delete_and_archive_reject_an_active_turn() {
    let temp = tempfile::tempdir().unwrap();
    let config = command_config(temp.path());
    let home = tempfile::tempdir().unwrap();
    let (mut core, _) = new_test_core(config, home.path());
    core.handshaken = true;
    let busy = core
        .session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap()["session_id"]
        .as_str()
        .unwrap()
        .to_owned();
    core.session_create(json!({"workspace": temp.path().to_string_lossy()}))
        .unwrap();
    core.sessions
        .lock()
        .unwrap()
        .get_mut(&busy)
        .unwrap()
        .active_turn = Some("turn-test".to_owned());

    let delete = core
        .session_delete(json!({"session_id": busy}))
        .unwrap_err();
    assert_eq!(delete.code, error_codes::TURN_IN_PROGRESS);
    let archive = core
        .session_archive(json!({"session_id": busy, "archived": true}))
        .unwrap_err();
    assert_eq!(archive.code, error_codes::TURN_IN_PROGRESS);
}
