from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess


ROOT = Path(__file__).resolve().parent.parent


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a self-signed certificate for --listen --tls-cert/--tls-key.",
    )
    parser.add_argument("--out-dir", default="tests/fixtures/tls")
    parser.add_argument("--subject", default="CN=localhost")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    openssl = shutil.which("openssl")
    if openssl is None:
        raise SystemExit(
            "openssl not found. Install OpenSSL or use Git for Windows, then re-run this script."
        )

    out_dir = Path(args.out_dir)
    if not out_dir.is_absolute():
        out_dir = ROOT / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    cert = out_dir / "cert.pem"
    key = out_dir / "key.pem"

    subject = args.subject if args.subject.startswith("/") else "/" + args.subject
    result = subprocess.run([
        openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes",
        "-keyout", str(key), "-out", str(cert), "-days", "3650",
        "-subj", subject,
        "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1",
    ])
    if result.returncode != 0:
        raise SystemExit(f"openssl failed with exit code {result.returncode}")

    fingerprint = subprocess.run(
        [openssl, "x509", "-in", str(cert), "-noout", "-fingerprint", "-sha256"],
        text=True, capture_output=True, check=True,
    ).stdout.strip().split("=", 1)[-1].replace(":", "")

    print(f"certificate: {cert}")
    print(f"private key: {key}")
    print(f"SHA-256 fingerprint: {fingerprint}")


main()
