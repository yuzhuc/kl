use kl_exec::CommandSpec;
use kl_policy::Action;
use kl_sandbox::{SandboxChild, SandboxError, SandboxManager, SandboxPolicy};
use kl_tools::{PolicyRequest, Tool, ToolContext, ToolError, ToolOutcome, ToolSource};
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::BTreeMap;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
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
    #[error("mcp sandbox error: {0}")]
    Sandbox(String),
}

struct Io {
    stdin: Box<dyn Write + Send>,
    lines: Receiver<String>,
    next_id: u64,
}

pub struct McpClient {
    io: Mutex<Io>,
    child: Mutex<Option<SandboxChild>>,
    timeout: Duration,
}

impl McpClient {
    pub fn connect(
        config: &McpServerConfig,
        timeout: Duration,
        sandbox: &SandboxManager,
        policy: &SandboxPolicy,
    ) -> Result<Self, McpError> {
        let spec = CommandSpec::new(
            &config.command,
            config.args.clone(),
            policy.workspace.clone(),
        );
        let mut child = sandbox.spawn(&spec, policy).map_err(sandbox_error)?;
        let stdin = child
            .stdin
            .take()
            .ok_or_else(|| McpError::Io(std::io::Error::other("mcp stdin unavailable")))?;
        let stdout = child
            .stdout
            .take()
            .ok_or_else(|| McpError::Io(std::io::Error::other("mcp stdout unavailable")))?;
        // The sandbox backends always pipe stderr; drain it so a chatty server
        // cannot fill the pipe and block on its own writes.
        if let Some(mut stderr) = child.stderr.take() {
            std::thread::spawn(move || {
                let _ = std::io::copy(&mut stderr, &mut std::io::sink());
            });
        }
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

fn sandbox_error(error: SandboxError) -> McpError {
    match error {
        SandboxError::Io(error) => McpError::Io(error),
        other => McpError::Sandbox(other.to_string()),
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
mod tests;
