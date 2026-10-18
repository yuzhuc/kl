param(
    [string]$OutputDir = "E:\kl",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

pwsh -NoProfile -File (Join-Path $PSScriptRoot "package.ps1") -Runtime $Runtime -OutputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "local package failed" }

Write-Host "Local build ready: $OutputDir"