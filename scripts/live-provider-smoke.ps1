$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$fixtureDir = Join-Path $root "tests\fixtures\providers"
New-Item -ItemType Directory -Force -Path $fixtureDir | Out-Null
$statusPath = Join-Path $fixtureDir "live-smoke.status.json"

function Invoke-LiveProvider {
    param(
        [string]$Name,
        [string]$KeyVariable,
        [string]$OutputFile
    )

    $key = [Environment]::GetEnvironmentVariable($KeyVariable)
    if ([string]::IsNullOrWhiteSpace($key)) {
        return [ordered]@{
            status = "pending"
            reason = "$KeyVariable is not set"
            fixture = $null
        }
    }

    Write-Host "== live smoke: $Name =="
    $output = & cargo run -q -p kl-provider --example live_smoke -- `
        --provider $Name `
        --out $OutputFile 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        return [ordered]@{
            status = "failed"
            reason = "live smoke exited with code $exitCode"
            fixture = $null
        }
    }

    return [ordered]@{
        status = "ok"
        reason = $null
        fixture = (Resolve-Path -LiteralPath $OutputFile).Path
    }
}

$openai = Invoke-LiveProvider -Name "openai" -KeyVariable "OPENAI_API_KEY" -OutputFile (Join-Path $fixtureDir "openai-live.json")
$anthropic = Invoke-LiveProvider -Name "anthropic" -KeyVariable "ANTHROPIC_API_KEY" -OutputFile (Join-Path $fixtureDir "anthropic-live.json")

$status = [ordered]@{
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    note = "Live fixtures are written only when the real provider call succeeds. Skips are recorded as pending, never fabricated."
    providers = [ordered]@{
        openai = $openai
        anthropic = $anthropic
    }
}
[IO.File]::WriteAllText($statusPath, ($status | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
Write-Host "status: $statusPath"

if ($openai.status -eq "pending" -and $anthropic.status -eq "pending") {
    Write-Host "Live provider smoke is pending: set OPENAI_API_KEY and/or ANTHROPIC_API_KEY, then rerun this script."
    exit 0
}
if ($openai.status -eq "failed" -or $anthropic.status -eq "failed") {
    exit 1
}
