param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

cargo build --release -p kl-core
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

$output = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $root "dist\$Runtime"
} else {
    [System.IO.Path]::GetFullPath($OutputDir)
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet publish "tui\Kl.Tui\Kl.Tui.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Diagnostics-only runtime files that a self-contained publish copies from the
# runtime pack. Nothing in the shipped binary needs them.
$diagnosticPattern = '\.pdb$|\.dbg$|^createdump(\.exe)?$|^Microsoft\.DiaSymReader\.Native\..*\.dll$|^mscordaccore.*\.dll$|^mscordbi.*\.dll$|^sos\.dll$|^libsosplugin|^libmscordaccore|^libmscordbi'
Get-ChildItem -LiteralPath $output -File |
    Where-Object { $_.Name -match $diagnosticPattern } |
    Remove-Item -Force

$coreName = if ($Runtime.StartsWith("win", [StringComparison]::OrdinalIgnoreCase)) {
    "kl-core.exe"
} else {
    "kl-core"
}
$coreSource = Join-Path $root "target\release\$coreName"
if (-not (Test-Path -LiteralPath $coreSource)) {
    throw "core binary not found: $coreSource"
}
Copy-Item -LiteralPath $coreSource -Destination (Join-Path $output $coreName) -Force
Copy-Item -LiteralPath (Join-Path $root "schemas") -Destination $output -Recurse -Force

Write-Host "Published bundle: $output"
