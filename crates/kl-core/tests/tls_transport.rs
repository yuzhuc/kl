//! TLS transport: `--listen --tls-cert/--tls-key` serves the same protocol
//! over rustls; clients that do not trust the certificate fail the handshake.

use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::{ClientConfig, ClientConnection, DigitallySignedStruct, SignatureScheme, StreamOwned};
use serde_json::{Value, json};
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::process::{Child, Command, Stdio};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};
use tempfile::TempDir;

const CERT_PEM: &str = include_str!("../../../tests/fixtures/tls/cert.pem");
const KEY_PEM: &str = include_str!("../../../tests/fixtures/tls/key.pem");

fn pem_certificate(pem: &str) -> Vec<u8> {
    use base64::Engine;
    let body = pem
        .split("-----BEGIN CERTIFICATE-----")
        .nth(1)
        .and_then(|rest| rest.split("-----END CERTIFICATE-----").next())
        .expect("fixture certificate");
    let encoded: String = body
        .chars()
        .filter(|character| !character.is_whitespace())
        .collect();
    base64::engine::general_purpose::STANDARD
        .decode(encoded)
        .expect("certificate base64")
}

struct TlsServer {
    child: Child,
    _temp: TempDir,
    address: String,
}

impl TlsServer {
    #[allow(clippy::zombie_processes)]
    fn start() -> Self {
        let temp = tempfile::tempdir().unwrap();
        let home = temp.path().join(".kl");
        std::fs::create_dir_all(&home).unwrap();
        std::fs::write(temp.path().join("script.json"), r#"[{"text":"hello"}]"#).unwrap();
        std::fs::write(
            temp.path().join("kl.toml"),
            r#"
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
"#,
        )
        .unwrap();
        let cert = temp.path().join("cert.pem");
        let key = temp.path().join("key.pem");
        std::fs::write(&cert, CERT_PEM).unwrap();
        std::fs::write(&key, KEY_PEM).unwrap();
        let child = Command::new(env!("CARGO_BIN_EXE_kl-core"))
            .arg("--serve")
            .arg("--home")
            .arg(&home)
            .arg("--config")
            .arg(temp.path().join("kl.toml"))
            .arg("--listen")
            .arg("127.0.0.1:0")
            .arg("--token")
            .arg("s3cret")
            .arg("--tls-cert")
            .arg(&cert)
            .arg("--tls-key")
            .arg(&key)
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .expect("start kl-core tls server");

        let server_file = home.join("server.json");
        let deadline = Instant::now() + Duration::from_secs(10);
        while Instant::now() < deadline {
            if let Ok(text) = std::fs::read_to_string(&server_file) {
                if let Ok(value) = serde_json::from_str::<Value>(&text) {
                    if value["transport"] == "tls" {
                        let endpoint = value["endpoint"].as_str().unwrap_or_default();
                        let address = endpoint.trim_start_matches("tls://").to_owned();
                        return Self {
                            child,
                            _temp: temp,
                            address,
                        };
                    }
                }
            }
            thread::sleep(Duration::from_millis(50));
        }
        panic!("server.json was not created");
    }

    fn connect(&self, trust_certificate: bool) -> std::io::Result<TlsClient> {
        let expected = trust_certificate.then(|| sha256(&pem_certificate(CERT_PEM)));
        let config = ClientConfig::builder()
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(FingerprintVerifier { expected }))
            .with_no_client_auth();
        let socket = TcpStream::connect(&self.address)?;
        socket.set_read_timeout(Some(Duration::from_secs(5)))?;
        let connection =
            ClientConnection::new(Arc::new(config), ServerName::try_from("localhost").unwrap())
                .unwrap();
        let stream = StreamOwned::new(connection, socket);
        Ok(TlsClient {
            writer: stream,
            next_id: 1,
        })
    }
}

fn sha256(bytes: &[u8]) -> [u8; 32] {
    use sha2::{Digest, Sha256};
    Sha256::digest(bytes).into()
}

/// The fixture certificate is self-signed, so the test pins its SHA-256
/// fingerprint the same way the .NET client does.
#[derive(Debug)]
struct FingerprintVerifier {
    expected: Option<[u8; 32]>,
}

impl ServerCertVerifier for FingerprintVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        if self.expected == Some(sha256(end_entity.as_ref())) {
            Ok(ServerCertVerified::assertion())
        } else {
            Err(rustls::Error::General(
                "certificate fingerprint mismatch".into(),
            ))
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        certificate: &CertificateDer<'_>,
        signed: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls12_signature(
            message,
            certificate,
            signed,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        certificate: &CertificateDer<'_>,
        signed: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls13_signature(
            message,
            certificate,
            signed,
            &rustls::crypto::ring::default_provider().signature_verification_algorithms,
        )
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        rustls::crypto::ring::default_provider()
            .signature_verification_algorithms
            .supported_schemes()
    }
}

impl Drop for TlsServer {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

struct TlsClient {
    writer: StreamOwned<ClientConnection, TcpStream>,
    next_id: i64,
}

impl TlsClient {
    fn request(&mut self, method: &str, params: Value) -> std::io::Result<Value> {
        let id = self.next_id;
        self.next_id += 1;
        let request = json!({
            "jsonrpc": "2.0",
            "id": id,
            "method": method,
            "params": params,
        });
        writeln!(self.writer, "{}", serde_json::to_string(&request).unwrap())?;
        self.writer.flush()?;
        let mut reader = BufReader::new(&mut self.writer);
        loop {
            let mut line = String::new();
            reader.read_line(&mut line)?;
            let value: Value = serde_json::from_str(&line).unwrap();
            if value.get("id") == Some(&json!(id)) {
                return Ok(value);
            }
        }
    }
}

fn handshake() -> Value {
    json!({
        "client_name": "tls-test",
        "client_version": "0.1.0",
        "protocol_versions": [1],
        "token": "s3cret",
    })
}

#[test]
fn tls_server_serves_the_protocol() {
    let server = TlsServer::start();
    let mut client = server.connect(true).expect("connect tls core");

    let accepted = client.request("kl.handshake", handshake()).unwrap();
    assert!(accepted.get("error").is_none(), "{accepted}");

    let listed = client.request("session.list", json!({})).unwrap();
    assert!(listed.get("error").is_none(), "{listed}");
}

#[test]
fn tls_server_rejects_an_untrusted_client() {
    let server = TlsServer::start();
    let mut client = server.connect(false).expect("connect tls core");
    let result = client.request("kl.handshake", handshake());
    assert!(
        result.is_err(),
        "untrusted client should fail the handshake"
    );
}
