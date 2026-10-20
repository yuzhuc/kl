//! Minimal stdio MCP server used by the sandbox tests.

use serde_json::{Value, json};
use std::io::{BufRead, Write};

fn main() {
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
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
            Some("tools/call") => json!({"content": [{"type": "text", "text": "hello"}]}),
            _ => json!({}),
        };
        let response = json!({"jsonrpc": "2.0", "id": id, "result": result});
        let _ = writeln!(stdout, "{response}");
        let _ = stdout.flush();
    }
}
