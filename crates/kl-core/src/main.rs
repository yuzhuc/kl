use kl_core::{Emitter, run_server, run_with_io_args};
use kl_protocol::CORE_VERSION;
use std::io::{self, BufReader};
use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut config = None;
    let mut home = None;
    let mut serve = false;
    let mut socket = None;
    let mut args = std::env::args().skip(1);
    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--version" | "-V" => {
                println!("kl-core {CORE_VERSION}");
                return ExitCode::SUCCESS;
            }
            "--config" => match args.next() {
                Some(path) => config = Some(PathBuf::from(path)),
                None => {
                    eprintln!("kl-core: --config requires a path");
                    return ExitCode::from(2);
                }
            },
            "--home" => match args.next() {
                Some(path) => home = Some(PathBuf::from(path)),
                None => {
                    eprintln!("kl-core: --home requires a path");
                    return ExitCode::from(2);
                }
            },
            "--serve" => serve = true,
            "--socket" => match args.next() {
                Some(name) if !name.trim().is_empty() => socket = Some(name),
                Some(_) => {
                    eprintln!("kl-core: --socket requires a non-empty name");
                    return ExitCode::from(2);
                }
                None => {
                    eprintln!("kl-core: --socket requires a name");
                    return ExitCode::from(2);
                }
            },
            other => {
                eprintln!("kl-core: unknown argument: {other}");
                return ExitCode::from(2);
            }
        }
    }

    if serve {
        return match run_server(config, home, socket) {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("kl-core: {error}");
                ExitCode::FAILURE
            }
        };
    }

    let stdin = io::stdin();
    let stdout = io::stdout();
    match run_with_io_args(
        BufReader::new(stdin.lock()),
        Emitter::new(Box::new(stdout)),
        config,
        home,
    ) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("kl-core: {error}");
            ExitCode::FAILURE
        }
    }
}
