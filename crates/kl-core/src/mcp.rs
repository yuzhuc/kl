use kl_policy::Action;
use kl_tools::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, ToolSource};
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const CONFIG_FILE: &str = "mcp.toml";

#[derive(Debug, Clone, Deserialize, Default)]
pub struct McpConfig {
    #[serde(default)]
    pub servers: BTreeMap<String, McpServerConfig>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct McpServerConfig {
    pub command: String,
    #[serde(default)]
    pub args: Vec<String>,
    #[serde(default = "enabled_by_default")]
    pub enabled: bool,
}

fn enabled_by_default() -> bool {
    true
}

impl McpConfig {
    pub fn enabled_servers(&self) -> impl Iterator<Item = (&str, &McpServerConfig)> {
        self.servers
            .iter()
            .filter(|(_, server)| server.enabled)
            .map(|(name, server)| (name.as_str(), server))
    }
}

pub fn load_config(home: &Path) -> Result<McpConfig, McpError> {
    let path = home.join(CONFIG_FILE);
    if !path.exists() {
        return Ok(McpConfig::default());
    }
    let text = std::fs::read_to_string(path)?;
    Ok(toml::from_str(&text)?)
}

#[derive(Debug, thiserror::Error)]
pub enum McpError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("invalid mcp config: {0}")]
    Config(#[from] toml::de::Error),
    #[error("mcp server timed out waiting for {0}")]
    Timeout(String),
    #[error("mcp server error: {0}")]
    Server(String),
    #[error("invalid mcp response: {0}")]
    Protocol(String),
}

struct Io {
    stdin: Box<dyn Write + Send>,
    lines: Receiver<String>,
    next_id: u64,
}

pub struct McpClient {
    io: Mutex<Io>,
    child: Mutex<Option<Child>>,
    timeout: Duration,
}

impl McpClient {
    pub fn connect(config: &McpServerConfig, timeout: Duration) -> Result<Self, McpError> {
        let mut child = Command::new(&config.command)
            .args(&config.args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| McpError::Io(std::io::Error::other("mcp stdin unavailable")))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| McpError::Io(std::io::Error::other("mcp stdout unavailable")))?;
        let (sender, lines) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { return };
                if sender.send(line).is_err() {
                    return;
                }
            }
        });
        let client = Self {
            io: Mutex::new(Io {
                stdin: Box::new(stdin),
                lines,
                next_id: 1,
            }),
            child: Mutex::new(Some(child)),
            timeout,
        };
        client.request(
            "initialize",
            json!({
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "kl", "version": env!("CARGO_PKG_VERSION")}
            }),
        )?;
        client.notify("notifications/initialized", json!({}))?;
        Ok(client)
    }

    pub fn list_tools(&self) -> Result<Vec<McpToolSpec>, McpError> {
        let result = self.request("tools/list", json!({}))?;
        let tools = result
            .get("tools")
            .and_then(Value::as_array)
            .ok_or_else(|| McpError::Protocol("tools/list returned no tools array".to_owned()))?;
        Ok(tools
            .iter()
            .filter_map(|tool| {
                let name = tool.get("name")?.as_str()?.to_owned();
                Some(McpToolSpec {
                    name,
                    description: tool
                        .get("description")
                        .and_then(Value::as_str)
                        .unwrap_or_default()
                        .to_owned(),
                    parameters: tool
                        .get("inputSchema")
                        .cloned()
                        .unwrap_or_else(|| json!({"type": "object"})),
                })
            })
            .collect())
    }

    pub fn call_tool(
        &self,
        name: &str,
        arguments: &Value,
        timeout: Duration,
    ) -> Result<Value, McpError> {
        self.request_with_timeout(
            "tools/call",
            json!({"name": name, "arguments": arguments}),
            timeout,
        )
    }

    fn notify(&self, method: &str, params: Value) -> Result<(), McpError> {
        let mut io = self.io.lock().unwrap_or_else(|error| error.into_inner());
        writeln!(
            io.stdin,
            "{}",
            json!({"jsonrpc": "2.0", "method": method, "params": params})
        )?;
        io.stdin.flush()?;
        Ok(())
    }

    fn request(&self, method: &str, params: Value) -> Result<Value, McpError> {
        self.request_with_timeout(method, params, self.timeout)
    }

    fn request_with_timeout(
        &self,
        method: &str,
        params: Value,
        timeout: Duration,
    ) -> Result<Value, McpError> {
        let mut io = self.io.lock().unwrap_or_else(|error| error.into_inner());
        let id = io.next_id;
        io.next_id += 1;
        writeln!(
            io.stdin,
            "{}",
            json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params})
        )?;
        io.stdin.flush()?;
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(McpError::Timeout(method.to_owned()));
            }
            let line = match io.lines.recv_timeout(remaining) {
                Ok(line) => line,
                Err(RecvTimeoutError::Timeout) => return Err(McpError::Timeout(method.to_owned())),
                Err(RecvTimeoutError::Disconnected) => {
                    return Err(McpError::Protocol("mcp server closed stdout".to_owned()));
                }
            };
            let Ok(message) = serde_json::from_str::<Value>(&line) else {
                continue;
            };
            if message.get("id").and_then(Value::as_u64) != Some(id) {
                continue;
            }
            if let Some(error) = message.get("error") {
                return Err(McpError::Server(error.to_string()));
            }
            return Ok(message.get("result").cloned().unwrap_or(Value::Null));
        }
    }
}

impl Drop for McpClient {
    fn drop(&mut self) {
        if let Some(mut child) = self
            .child
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .take()
        {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

#[derive(Debug, Clone)]
pub struct McpToolSpec {
    pub name: String,
    pub description: String,
    pub parameters: Value,
}

pub struct McpTool {
    name: String,
    description: String,
    parameters: Value,
    server: String,
    tool: String,
    client: Arc<McpClient>,
}

impl McpTool {
    pub fn new(server: &str, spec: McpToolSpec, client: Arc<McpClient>) -> Self {
        Self {
            name: tool_name(server, &spec.name),
            description: spec.description,
            parameters: spec.parameters,
            server: server.to_owned(),
            tool: spec.name,
            client,
        }
    }
}

impl Tool for McpTool {
    fn name(&self) -> &str {
        &self.name
    }

    fn description(&self) -> &str {
        &self.description
    }

    fn parameters(&self) -> Value {
        self.parameters.clone()
    }

    fn policy_request(&self, _args: &Value) -> Result<PolicyRequest, ToolError> {
        Ok(PolicyRequest {
            action: Action::Plugin,
            resource: format!("mcp:{}:{}", self.server, self.tool),
        })
    }

    fn execute(&self, context: &ToolContext<'_>, args: &Value) -> Result<ToolOutcome, ToolError> {
        let result = self
            .client
            .call_tool(&self.tool, args, context.timeout)
            .map_err(|error| ToolError::Execution(error.to_string()))?;
        let text = content_text(&result);
        let output = if text.is_empty() {
            serde_json::to_string(&result)
                .map_err(|error| ToolError::Execution(error.to_string()))?
        } else {
            text
        };
        if result
            .get("isError")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        {
            return Err(ToolError::Execution(output));
        }
        Ok(ToolOutcome::ok(
            format!("called mcp tool {}", self.tool),
            output,
        ))
    }

    fn source(&self) -> ToolSource {
        ToolSource::Mcp
    }

    fn max_result_chars(&self) -> usize {
        30_000
    }
}

fn content_text(result: &Value) -> String {
    result
        .get("content")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| item.get("text").and_then(Value::as_str))
                .collect::<Vec<_>>()
                .join("\n")
        })
        .unwrap_or_default()
}

pub fn tool_name(server: &str, tool: &str) -> String {
    format!("mcp__{}__{}", sanitize(server), sanitize(tool))
}

fn sanitize(value: &str) -> String {
    value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() {
                character
            } else {
                '_'
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc::{Receiver, Sender};
    use std::thread::JoinHandle;

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
                let _ = responses
                    .send(json!({"jsonrpc": "2.0", "id": id, "result": result}).to_string());
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
            kl_policy::PolicyEngine::new(workspace.path(), kl_policy::PolicyConfig::default())
                .unwrap();
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
}
