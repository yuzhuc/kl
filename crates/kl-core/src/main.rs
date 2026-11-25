use kl_core::{Emitter, run_server, run_tcp_server, run_with_io_args};
use kl_protocol::CORE_VERSION;
use std::io::{self, BufReader};
use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let mut config = None;
    let mut home = None;
    let mut serve = false;
    let mut socket = None;
    let mut listen = None;
    let mut token = None;
    let mut tls_cert = None;
    let mut tls_key = None;
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
            "--listen" => match args.next() {
                Some(address) if !address.trim().is_empty() => listen = Some(address),
                Some(_) => {
                    eprintln!("kl-core: --listen requires an address");
                    return ExitCode::from(2);
                }
                None => {
                    eprintln!("kl-core: --listen requires an address");
                    return ExitCode::from(2);
                }
            },
            "--token" => match args.next() {
                Some(value) if !value.trim().is_empty() => token = Some(value),
                Some(_) => {
                    eprintln!("kl-core: --token requires a value");
                    return ExitCode::from(2);
                }
                None => {
                    eprintln!("kl-core: --token requires a value");
                    return ExitCode::from(2);
                }
            },
            "--tls-cert" => match args.next() {
                Some(path) if !path.trim().is_empty() => tls_cert = Some(PathBuf::from(path)),
                Some(_) => {
                    eprintln!("kl-core: --tls-cert requires a path");
                    return ExitCode::from(2);
                }
                None => {
                    eprintln!("kl-core: --tls-cert requires a path");
                    return ExitCode::from(2);
                }
            },
            "--tls-key" => match args.next() {
                Some(path) if !path.trim().is_empty() => tls_key = Some(PathBuf::from(path)),
                Some(_) => {
                    eprintln!("kl-core: --tls-key requires a path");
                    return ExitCode::from(2);
                }
                None => {
                    eprintln!("kl-core: --tls-key requires a path");
                    return ExitCode::from(2);
                }
            },
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

    let tls = match (tls_cert, tls_key) {
        (Some(certificate), Some(key)) => Some((certificate, key)),
        (None, None) => None,
        _ => {
            eprintln!("kl-core: --tls-cert and --tls-key must be used together");
            return ExitCode::from(2);
        }
    };

    if tls.is_some() && listen.is_none() {
        eprintln!("kl-core: --tls-cert/--tls-key require --listen");
        return ExitCode::from(2);
    }

    if let Some(address) = listen {
        if socket.is_some() {
            eprintln!("kl-core: use either --socket or --listen, not both");
            return ExitCode::from(2);
        }
        if !is_loopback_address(&address) && token.is_none() {
            eprintln!(
                "kl-core: --token is required when --listen is not on loopback; the remote API can run tools"
            );
            return ExitCode::from(2);
        }
        return match run_tcp_server(config, home, &address, token, tls) {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => {
                eprintln!("kl-core: {error}");
                ExitCode::FAILURE
            }
        };
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

fn is_loopback_address(address: &str) -> bool {
    let host = address
        .rsplit_once(':')
        .map(|(host, _)| host)
        .unwrap_or(address)
        .trim_matches(['[', ']']);
    host.eq_ignore_ascii_case("localhost")
        || host
            .parse::<std::net::IpAddr>()
            .map(|ip| ip.is_loopback())
            .unwrap_or(false)
}
