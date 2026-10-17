$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== rust format =="
cargo fmt --all -- --check
if ($LASTEXITCODE -ne 0) { throw "cargo fmt failed" }

Write-Host "== rust clippy =="
cargo clippy --workspace --all-targets -- -D warnings
if ($LASTEXITCODE -ne 0) { throw "cargo clippy failed" }

Write-Host "== rust tests =="
cargo test --workspace
if ($LASTEXITCODE -ne 0) { throw "cargo test failed" }

Write-Host "== rust build =="
cargo build --workspace
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

Write-Host "== dotnet build =="
dotnet build kl.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "== dotnet tests =="
dotnet test kl.sln --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }

Write-Host "All checks passed."
