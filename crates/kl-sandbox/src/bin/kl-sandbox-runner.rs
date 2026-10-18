use serde::Deserialize;
use std::path::PathBuf;
use std::process::{Command, Stdio};

#[derive(Debug, Deserialize)]
struct Config {
    program: String,
    args: Vec<String>,
    cwd: PathBuf,
    clear_env: bool,
    env: Vec<(String, String)>,
}

fn main() {
    let Some(path) = std::env::args_os().nth(1) else {
        std::process::exit(127);
    };
    let Ok(text) = std::fs::read_to_string(&path) else {
        std::process::exit(127);
    };
    let Ok(config) = serde_json::from_str::<Config>(&text) else {
        std::process::exit(127);
    };
    let _ = std::fs::remove_file(&path);

    if config.clear_env {
        let names: Vec<_> = std::env::vars_os().map(|(name, _)| name).collect();
        for name in names {
            unsafe {
                std::env::remove_var(name);
            }
        }
    }
    for (name, value) in config.env {
        unsafe {
            std::env::set_var(name, value);
        }
    }

    let status = Command::new(&config.program)
        .args(&config.args)
        .current_dir(&config.cwd)
        .stdin(Stdio::inherit())
        .stdout(Stdio::inherit())
        .stderr(Stdio::inherit())
        .status();
    match status {
        Ok(status) => std::process::exit(status.code().unwrap_or(1)),
        Err(error) => {
            eprintln!(
                "kl-sandbox-runner: cannot start {}: {error}",
                config.program
            );
            std::process::exit(127);
        }
    }
}
