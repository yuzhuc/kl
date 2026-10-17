$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

cargo build --workspace
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
dotnet build kl.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$target = Join-Path $root "target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
$targetFull = (Resolve-Path $target).Path
$demo = Join-Path $targetFull "policy-demo"
if (-not $demo.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to operate outside target/: $demo"
}
if (Test-Path -LiteralPath $demo) {
    Remove-Item -LiteralPath $demo -Recurse -Force
}
New-Item -ItemType Directory -Path $demo | Out-Null
Copy-Item -Path (Join-Path $root "examples\policy-demo-template\*") -Destination $demo -Recurse -Force

$kl = Join-Path $root "cli\Kl.Cli\bin\Debug\net10.0\kl.exe"
$config = Join-Path $demo "kl.toml"
$klHome = Join-Path $demo ".kl"

Write-Host "== deny checks (even with --yes) =="
& $kl run "Try the dangerous operations" --workspace $demo --config $config --home $klHome --yes
if ($LASTEXITCODE -ne 0) { throw "kl run failed with exit code $LASTEXITCODE" }

$auditPath = Join-Path $klHome "audit\events.jsonl"
if (-not (Test-Path -LiteralPath $auditPath)) {
    throw "audit log was not created"
}
$records = @(Get-Content -LiteralPath $auditPath | ForEach-Object { $_ | ConvertFrom-Json })
$denied = @($records | Where-Object { $_.decision -eq "deny" })
if ($denied.Count -lt 2) {
    throw "expected at least 2 denied audit records, got $($denied.Count)"
}
foreach ($record in $denied) {
    if ($record.ok) { throw "denied record unexpectedly succeeded" }
    Write-Host ("denied: {0} -> {1}" -f $record.tool, $record.resource)
}

Write-Host "Policy demo passed: $demo"
