//! Live MCP stdio smoke: connect to every enabled server in `mcp.toml` and
//! list its tools. Reports `pending` when no server is configured and `failed`
//! when a configured server cannot be reached. Success is only reported after
//! a real handshake.

use kl_core::mcp::{McpClient, load_config};
use serde_json::json;
use std::io::{Error, ErrorKind};
use std::path::PathBuf;
use std::time::Duration;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut home = None;
    let mut output = None;
    let mut args = std::env::args().skip(1);
    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--home" => {
                home = Some(PathBuf::from(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--home requires a value")
                })?));
            }
            "--out" => {
                output = Some(PathBuf::from(args.next().ok_or_else(|| {
                    Error::new(ErrorKind::InvalidInput, "--out requires a value")
                })?));
            }
            other => {
                return Err(Error::new(
                    ErrorKind::InvalidInput,
                    format!("unknown argument: {other}"),
                )
                .into());
            }
        }
    }

    let home = home.unwrap_or_else(kl_core::kl_home);
    let config_path = home.join("mcp.toml");
    let config = match load_config(&home) {
        Ok(config) => config,
        Err(error) => {
            let record = json!({
                "status": "failed",
                "reason": format!("cannot read {}: {error}", config_path.display()),
                "servers": [],
            });
            println!("{}", serde_json::to_string(&record)?);
            std::process::exit(1);
        }
    };

    let servers = config.enabled_servers().collect::<Vec<_>>();
    if servers.is_empty() {
        let record = json!({
            "status": "pending",
            "reason": format!("no enabled MCP server in {}", config_path.display()),
            "servers": [],
        });
        println!("{}", serde_json::to_string(&record)?);
        return Ok(());
    }

    let mut reports = Vec::new();
    let mut failed = 0usize;
    for (name, server) in servers {
        let client = match McpClient::connect(server, Duration::from_secs(10)) {
            Ok(client) => client,
            Err(error) => {
                failed += 1;
                reports.push(json!({
                    "id": name,
                    "status": "failed",
                    "reason": error.to_string(),
                    "tools": [],
                }));
                continue;
            }
        };
        match client.list_tools() {
            Ok(specs) => reports.push(json!({
                "id": name,
                "status": "ok",
                "reason": serde_json::Value::Null,
                "tools": specs.iter().map(|spec| &spec.name).collect::<Vec<_>>(),
            })),
            Err(error) => {
                failed += 1;
                reports.push(json!({
                    "id": name,
                    "status": "failed",
                    "reason": error.to_string(),
                    "tools": [],
                }));
            }
        }
    }

    let record = if failed == 0 {
        json!({
            "status": "ok",
            "reason": serde_json::Value::Null,
            "servers": reports,
        })
    } else {
        json!({
            "status": "failed",
            "reason": format!("{failed} of {} MCP servers failed", reports.len()),
            "servers": reports,
        })
    };
    if failed == 0 {
        write_record(&record, output.as_deref())?;
    }
    println!("{}", serde_json::to_string(&record)?);
    Ok(())
}

fn write_record(
    record: &serde_json::Value,
    output: Option<&std::path::Path>,
) -> Result<(), Box<dyn std::error::Error>> {
    let Some(path) = output else {
        return Ok(());
    };
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(path, serde_json::to_vec_pretty(record)?)?;
    Ok(())
}
