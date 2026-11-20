use super::*;
use std::path::PathBuf;
use std::sync::mpsc::{Receiver, Sender};
use std::thread::JoinHandle;

fn fake_server_binary() -> PathBuf {
    option_env!("CARGO_BIN_EXE_fake_mcp_server")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            let exe = std::env::current_exe().expect("test executable path");
            let debug = exe
                .parent()
                .and_then(Path::parent)
                .expect("debug directory");
            debug.join(if cfg!(windows) {
                "fake_mcp_server.exe"
            } else {
                "fake_mcp_server"
            })
        })
}

fn fake_server_config() -> McpServerConfig {
    McpServerConfig {
        command: fake_server_binary().to_string_lossy().into_owned(),
        args: Vec::new(),
        enabled: true,
    }
}

struct LineWriter {
    sender: Sender<String>,
    buffer: Vec<u8>,
}

impl LineWriter {
    fn new(sender: Sender<String>) -> Self {
        Self {
            sender,
            buffer: Vec::new(),
        }
    }
}

impl Write for LineWriter {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.buffer.extend_from_slice(bytes);
        while let Some(end) = self.buffer.iter().position(|byte| *byte == b'\n') {
            let line: Vec<u8> = self.buffer.drain(..=end).collect();
            let line = String::from_utf8(line).expect("utf8 request");
            let _ = self.sender.send(line);
        }
        Ok(bytes.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}

fn spawn_fake_server() -> (McpClient, JoinHandle<()>) {
    let (request_sender, requests): (Sender<String>, Receiver<String>) = mpsc::channel();
    let (responses, response_receiver): (Sender<String>, Receiver<String>) = mpsc::channel();
    let handle = std::thread::spawn(move || {
        for line in requests {
            let Ok(message) = serde_json::from_str::<Value>(line.trim()) else {
                continue;
            };
            let Some(id) = message.get("id").cloned() else {
                continue;
            };
            let result = match message.get("method").and_then(Value::as_str) {
                Some("initialize") => {
                    json!({"protocolVersion": "2024-11-05", "capabilities": {}})
                }
                Some("tools/list") => json!({"tools": [{
                    "name": "echo",
                    "description": "Echo text",
                    "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}
                }]}),
                Some("tools/call") => {
                    if message
                        .pointer("/params/arguments/text")
                        .and_then(Value::as_str)
                        == Some("fail")
                    {
                        json!({"isError": true, "content": [{"type": "text", "text": "boom"}]})
                    } else {
                        json!({"content": [{"type": "text", "text": "hello"}]})
                    }
                }
                _ => json!({}),
            };
            let _ =
                responses.send(json!({"jsonrpc": "2.0", "id": id, "result": result}).to_string());
        }
    });
    let client = McpClient {
        io: Mutex::new(Io {
            stdin: Box::new(LineWriter::new(request_sender)),
            lines: response_receiver,
            next_id: 1,
        }),
        child: Mutex::new(None),
        timeout: Duration::from_secs(2),
    };
    (client, handle)
}

#[test]
fn missing_config_is_empty() {
    let home = tempfile::tempdir().unwrap();
    assert!(load_config(home.path()).unwrap().servers.is_empty());
}

#[test]
fn parses_servers_and_defaults_enabled_to_true() {
    let home = tempfile::tempdir().unwrap();
    std::fs::write(
        home.path().join(CONFIG_FILE),
        r#"
[servers.alpha]
command = "alpha-server"
args = ["--verbose"]

[servers.beta]
command = "beta-server"
enabled = false
"#,
    )
    .unwrap();
    let config = load_config(home.path()).unwrap();
    let alpha = &config.servers["alpha"];
    assert_eq!(alpha.command, "alpha-server");
    assert_eq!(alpha.args, vec!["--verbose"]);
    assert!(alpha.enabled);
    assert!(!config.servers["beta"].enabled);
}

#[test]
fn filters_disabled_servers() {
    let home = tempfile::tempdir().unwrap();
    std::fs::write(
        home.path().join(CONFIG_FILE),
        r#"
[servers.alpha]
command = "alpha-server"

[servers.beta]
command = "beta-server"
enabled = false
"#,
    )
    .unwrap();
    let config = load_config(home.path()).unwrap();
    let enabled: Vec<&str> = config.enabled_servers().map(|(name, _)| name).collect();
    assert_eq!(enabled, vec!["alpha"]);
}

#[test]
fn normalizes_tool_names() {
    assert_eq!(
        tool_name("rust-analyzer", "find.symbols"),
        "mcp__rust_analyzer__find_symbols"
    );
}

#[test]
fn client_initializes_lists_and_calls_tools() {
    let (client, handle) = spawn_fake_server();
    client.request("initialize", json!({})).unwrap();
    client
        .notify("notifications/initialized", json!({}))
        .unwrap();
    let specs = client.list_tools().unwrap();
    assert_eq!(specs.len(), 1);
    assert_eq!(specs[0].name, "echo");
    let result = client
        .call_tool("echo", &json!({"text": "hi"}), Duration::from_secs(2))
        .unwrap();
    assert_eq!(result["content"][0]["text"], "hello");
    drop(client);
    handle.join().unwrap();
}

#[test]
fn client_connects_through_the_policy_backend() {
    let workspace = tempfile::tempdir().unwrap();
    let manager = kl_sandbox::SandboxManager::policy_only();
    let policy = kl_sandbox::SandboxPolicy::workspace(workspace.path());
    let client = McpClient::connect(
        &fake_server_config(),
        Duration::from_secs(10),
        &manager,
        &policy,
    )
    .unwrap();
    let specs = client.list_tools().unwrap();
    assert_eq!(specs.len(), 1);
    assert_eq!(specs[0].name, "echo");
    let result = client
        .call_tool("echo", &json!({"text": "hi"}), Duration::from_secs(10))
        .unwrap();
    assert_eq!(result["content"][0]["text"], "hello");
}

#[cfg(windows)]
#[test]
fn client_connects_through_the_strict_backend() {
    let workspace = tempfile::tempdir().unwrap();
    let manager = kl_sandbox::SandboxManager::probe(&kl_sandbox::SandboxConfig {
        mode: kl_sandbox::SandboxMode::Strict,
        backend: kl_sandbox::SandboxBackendKind::AppContainer,
        ..kl_sandbox::SandboxConfig::default()
    });
    if !manager.status().available {
        eprintln!("AppContainer unavailable: {}", manager.status().detail);
        return;
    }
    let policy = kl_sandbox::SandboxPolicy::workspace(workspace.path());
    let client = McpClient::connect(
        &fake_server_config(),
        Duration::from_secs(10),
        &manager,
        &policy,
    )
    .unwrap();
    let specs = client.list_tools().unwrap();
    assert_eq!(specs.len(), 1);
    assert_eq!(specs[0].name, "echo");
}

#[test]
fn mcp_tool_uses_plugin_policy_and_mcp_source() {
    let (client, handle) = spawn_fake_server();
    let client = Arc::new(client);
    let spec = McpToolSpec {
        name: "echo".to_owned(),
        description: "Echo text".to_owned(),
        parameters: json!({"type": "object"}),
    };
    let tool = McpTool::new("fake-server", spec, client);
    assert_eq!(tool.name(), "mcp__fake_server__echo");
    assert_eq!(tool.source(), ToolSource::Mcp);
    assert!(!tool.read_only(&json!({})));
    assert_eq!(tool.max_result_chars(), 30_000);
    let request = tool.policy_request(&json!({})).unwrap();
    assert_eq!(request.action, Action::Plugin);
    assert_eq!(request.resource, "mcp:fake-server:echo");
    drop(tool);
    handle.join().unwrap();
}

#[test]
fn mcp_tool_maps_is_error_to_execution_error() {
    let (client, handle) = spawn_fake_server();
    let spec = McpToolSpec {
        name: "echo".to_owned(),
        description: "Echo text".to_owned(),
        parameters: json!({"type": "object"}),
    };
    let tool = McpTool::new("fake-server", spec, Arc::new(client));
    let workspace = tempfile::tempdir().unwrap();
    let policy =
        kl_policy::PolicyEngine::new(workspace.path(), kl_policy::PolicyConfig::default()).unwrap();
    let cancel = std::sync::atomic::AtomicBool::new(false);
    let context = ToolContext {
        policy: &policy,
        cancel: &cancel,
        sandbox: Arc::new(kl_sandbox::SandboxManager::policy_only()),
        timeout: Duration::from_secs(2),
        max_output_bytes: 64 * 1024,
    };

    let error = tool
        .execute(&context, &json!({"text": "fail"}))
        .unwrap_err();
    assert!(matches!(error, ToolError::Execution(message) if message.contains("boom")));

    drop(tool);
    handle.join().unwrap();
}
