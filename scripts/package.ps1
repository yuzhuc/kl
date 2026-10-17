param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

cargo build --release -p kl-core
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }

$output = Join-Path $root "dist\$Runtime"
dotnet publish "cli\Kl.Cli\Kl.Cli.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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
