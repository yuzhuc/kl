param(
    [string]$OutDir = "tests/fixtures/tls",
    [string]$Subject = "CN=localhost"
)

# Generates a self-signed certificate for --listen --tls-cert/--tls-key.
# Requires openssl (Git for Windows ships one); no NuGet or external tools.
$ErrorActionPreference = "Stop"
$openssl = Get-Command openssl -ErrorAction SilentlyContinue
if (-not $openssl) {
    throw "openssl not found. Install OpenSSL or use Git for Windows, then re-run this script."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$certPath = Join-Path $OutDir "cert.pem"
$keyPath = Join-Path $OutDir "key.pem"

& $openssl.Source req -x509 -newkey rsa:2048 -nodes `
    -keyout $keyPath -out $certPath -days 3650 `
    -subj $Subject `
    -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
if ($LASTEXITCODE -ne 0) {
    throw "openssl failed with exit code $LASTEXITCODE"
}

$fingerprint = (& $openssl.Source x509 -in $certPath -noout -fingerprint -sha256) -replace "SHA256 Fingerprint=", ""
Write-Output "certificate: $certPath"
Write-Output "private key: $keyPath"
Write-Output "SHA-256 fingerprint: $($fingerprint -replace ':','')"
