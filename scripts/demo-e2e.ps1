$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== build =="
cargo build --workspace
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
dotnet build kl.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$target = Join-Path $root "target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
$targetFull = (Resolve-Path $target).Path
$demo = Join-Path $targetFull "e2e-demo"
if (-not $demo.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to operate outside target/: $demo"
}
if (Test-Path -LiteralPath $demo) {
    Remove-Item -LiteralPath $demo -Recurse -Force
}
New-Item -ItemType Directory -Path $demo | Out-Null
Copy-Item -Path (Join-Path $root "examples\e2e-demo-template\*") -Destination $demo -Recurse -Force

$kl = Join-Path $root "cli\Kl.Cli\bin\Debug\net10.0\kl.exe"
$config = Join-Path $demo "kl.toml"
$klHome = Join-Path $demo ".kl"

Write-Host "== agent task =="
& $kl run "Fix the failing test and run cargo test" --workspace $demo --config $config --home $klHome --yes
if ($LASTEXITCODE -ne 0) { throw "kl run failed with exit code $LASTEXITCODE" }

$lib = Get-Content (Join-Path $demo "src\lib.rs") -Raw
if ($lib -notmatch "a \+ b") {
    throw "agent did not apply the expected edit"
}

Write-Host "== audit =="
& $kl audit 20 --config $config --home $klHome
if ($LASTEXITCODE -ne 0) { throw "kl audit failed" }

$auditPath = Join-Path $klHome "audit\events.jsonl"
if (-not (Test-Path -LiteralPath $auditPath)) {
    throw "audit log was not created"
}
$records = @(Get-Content -LiteralPath $auditPath)
if ($records.Count -lt 3) {
    throw "expected at least 3 audit records, got $($records.Count)"
}
$records | ForEach-Object {
    $record = $_ | ConvertFrom-Json
    if (-not $record.ok -and $record.decision -ne "deny") {
        throw "unexpected failed audit record: $_"
    }
}

Write-Host "E2E demo passed: $demo"
