//! Minimal TCP connect probe used by the AppContainer network isolation test.
//!
//! Exit codes: 0 connected, 1 connect denied or failed, 2 bad usage.

use std::net::{SocketAddr, TcpStream};
use std::process::ExitCode;
use std::time::Duration;

fn main() -> ExitCode {
    let Some(target) = std::env::args().nth(1) else {
        eprintln!("usage: kl-net-probe <host:port>");
        return ExitCode::from(2);
    };
    let Ok(address) = target.parse::<SocketAddr>() else {
        eprintln!("kl-net-probe: invalid address: {target}");
        return ExitCode::from(2);
    };
    match TcpStream::connect_timeout(&address, Duration::from_secs(3)) {
        Ok(_) => {
            println!("connected {address}");
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("connect failed: {:?}: {error}", error.kind());
            ExitCode::from(1)
        }
    }
}
