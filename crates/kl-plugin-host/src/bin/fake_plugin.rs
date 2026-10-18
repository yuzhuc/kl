use serde::Deserialize;
use serde_json::{Value, json};
use std::io::{BufRead, Write};
use std::time::Duration;

#[derive(Debug, Default, Deserialize)]
struct FakeConfig {
    #[serde(default)]
    startup_delay_ms: u64,
    #[serde(default)]
    fail_handshake: bool,
    #[serde(default)]
    fail_health: bool,
    #[serde(default)]
    crash_on_execute: bool,
    #[serde(default)]
    sleep_ms: u64,
    #[serde(default)]
    provider_text: Option<String>,
    #[serde(default)]
    version: Option<String>,
    #[serde(default)]
    expected_token: Option<String>,
    #[serde(default)]
    escape_read: Option<String>,
}

fn main() {
    let token = std::env::var("KL_PLUGIN_TOKEN").unwrap_or_default();
    let plugin_id = std::env::var("KL_PLUGIN_ID").unwrap_or_else(|_| "fake-plugin".to_owned());
    let env_version = std::env::var("KL_PLUGIN_VERSION").unwrap_or_else(|_| "0.0.0".to_owned());
    let config = load_config();
    let version = config.version.clone().unwrap_or(env_version);

    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else {
            break;
        };
        let Ok(message) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        let Some(id) = message.get("id").cloned() else {
            continue;
        };
        let method = message
            .get("method")
            .and_then(Value::as_str)
            .unwrap_or_default();
        let params = message.get("params").cloned().unwrap_or(Value::Null);

        let response = match method {
            "plugin.handshake" => handshake(&config, &token, &plugin_id, &version, &params, id),
            "plugin.health" => health(&config, &token, &plugin_id, &version, &params, id),
            "provider.chat" => provider_chat(&config, &token, &version, &params, id),
            "tool.execute" => tool_execute(&config, &token, &version, &params, id),
            other => error(id, -32601, format!("unknown method: {other}")),
        };
        serde_json::to_writer(&mut stdout, &response).ok();
        let _ = stdout.write_all(b"\n");
        let _ = stdout.flush();
    }
}

fn load_config() -> FakeConfig {
    let Some(path) = std::env::var_os("KL_PLUGIN_CONFIG") else {
        return FakeConfig::default();
    };
    let Ok(text) = std::fs::read_to_string(path) else {
        return FakeConfig::default();
    };
    serde_json::from_str(&text).unwrap_or_default()
}

fn verify_token(config: &FakeConfig, token: &str, params: &Value) -> Result<(), Value> {
    let expected = config.expected_token.as_deref().unwrap_or(token);
    if expected.is_empty() || params.get("token").and_then(Value::as_str) != Some(expected) {
        return Err(json!({
            "code": -32001,
            "message": "capability token rejected",
        }));
    }
    Ok(())
}

fn handshake(
    config: &FakeConfig,
    token: &str,
    plugin_id: &str,
    version: &str,
    params: &Value,
    id: Value,
) -> Value {
    if config.startup_delay_ms > 0 {
        std::thread::sleep(Duration::from_millis(config.startup_delay_ms));
    }
    if config.fail_handshake {
        return error(id, -32000, "fake handshake failure");
    }
    if let Err(error) = verify_token(config, token, params) {
        return json!({"jsonrpc": "2.0", "id": id, "error": error});
    }
    success(
        id,
        json!({
            "ok": true,
            "api": 1,
            "id": plugin_id,
            "version": version,
            "token": token,
        }),
    )
}

fn health(
    config: &FakeConfig,
    token: &str,
    plugin_id: &str,
    version: &str,
    params: &Value,
    id: Value,
) -> Value {
    if let Err(error) = verify_token(config, token, params) {
        return json!({"jsonrpc": "2.0", "id": id, "error": error});
    }
    if config.fail_health {
        return success(
            id,
            json!({"ok": false, "id": plugin_id, "version": version}),
        );
    }
    success(id, json!({"ok": true, "id": plugin_id, "version": version}))
}

fn provider_chat(
    config: &FakeConfig,
    token: &str,
    version: &str,
    params: &Value,
    id: Value,
) -> Value {
    if let Err(error) = verify_token(config, token, params) {
        return json!({"jsonrpc": "2.0", "id": id, "error": error});
    }
    let text = config
        .provider_text
        .clone()
        .unwrap_or_else(|| format!("fake provider {version}"));
    success(
        id,
        json!({
            "text": text,
            "tool_calls": [],
            "finish_reason": "stop",
            "usage": {"input_tokens": 1, "output_tokens": 1},
        }),
    )
}

fn tool_execute(
    config: &FakeConfig,
    token: &str,
    version: &str,
    params: &Value,
    id: Value,
) -> Value {
    if let Err(error) = verify_token(config, token, params) {
        return json!({"jsonrpc": "2.0", "id": id, "error": error});
    }
    if config.crash_on_execute {
        std::process::exit(17);
    }
    if config.sleep_ms > 0 {
        std::thread::sleep(Duration::from_millis(config.sleep_ms));
    }
    let args = params.get("args").cloned().unwrap_or_else(|| json!({}));
    let output = if let Some(path) = config.escape_read.as_deref() {
        match std::fs::read_to_string(path) {
            Ok(text) => format!("escape-read-ok:{text}"),
            Err(error) => format!("escape-read-denied:{error}"),
        }
    } else if let Some(name) = args.get("env_name").and_then(Value::as_str) {
        format!(
            "env:{name}={}",
            std::env::var(name).unwrap_or_else(|_| "<missing>".to_owned())
        )
    } else if let Some(url) = args.get("url").and_then(Value::as_str) {
        format!("network:{url}")
    } else if let Some(path) = args.get("path").and_then(Value::as_str) {
        format!("path:{path}")
    } else {
        format!("fake tool {version} ok")
    };
    success(
        id,
        json!({
            "success": true,
            "summary": format!("fake plugin {version} executed"),
            "output": output,
            "diff": null,
        }),
    )
}

fn success(id: Value, result: Value) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "result": result})
}

fn error(id: Value, code: i64, message: impl Into<String>) -> Value {
    json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": {"code": code, "message": message.into()},
    })
}
