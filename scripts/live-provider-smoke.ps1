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
        --protocol $Name `
        --key-env $KeyVariable `
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

$results = [ordered]@{
    "openai-chat" = Invoke-LiveProvider -Name "openai-chat" -KeyVariable "OPENAI_API_KEY" -OutputFile (Join-Path $fixtureDir "openai-chat-live.json")
    "anthropic" = Invoke-LiveProvider -Name "anthropic" -KeyVariable "ANTHROPIC_API_KEY" -OutputFile (Join-Path $fixtureDir "anthropic-live.json")
    "openai-responses" = Invoke-LiveProvider -Name "openai-responses" -KeyVariable "OPENAI_API_KEY" -OutputFile (Join-Path $fixtureDir "openai-responses-live.json")
}

$status = [ordered]@{
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    note = "Live fixtures are written only when the real provider call succeeds. Skips are recorded as pending, never fabricated."
    providers = $results
}
[IO.File]::WriteAllText($statusPath, ($status | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
Write-Host "status: $statusPath"

$failed = @($results.Values | Where-Object { $_.status -eq "failed" })
if ($failed.Count -gt 0) {
    exit 1
}
$pending = @($results.Values | Where-Object { $_.status -eq "pending" })
if ($pending.Count -eq $results.Count) {
    Write-Host "Live provider smoke is pending: set OPENAI_API_KEY and/or ANTHROPIC_API_KEY, then rerun this script."
}
