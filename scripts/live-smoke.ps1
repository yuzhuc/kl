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

function Invoke-LiveMcp {
    param([string]$OutputFile)

    Write-Host "== live smoke: mcp =="
    $output = & cargo run -q -p kl-core --example mcp_smoke -- `
        --out $OutputFile 2>&1
    $output | ForEach-Object { Write-Host $_ }

    $record = $output | ForEach-Object {
        $text = "$_"
        if (-not $text.TrimStart().StartsWith("{")) { return }
        try { $text | ConvertFrom-Json } catch { return }
    } | Select-Object -Last 1
    if ($null -eq $record) {
        return [ordered]@{
            status = "failed"
            reason = "MCP smoke produced no status record"
            fixture = $null
            servers = @()
        }
    }

    return [ordered]@{
        status = $record.status
        reason = $record.reason
        fixture = if ($record.status -eq "ok") { (Resolve-Path -LiteralPath $OutputFile).Path } else { $null }
        servers = $record.servers
    }
}

$providers = [ordered]@{
    "openai-chat" = Invoke-LiveProvider -Name "openai-chat" -KeyVariable "OPENAI_API_KEY" -OutputFile (Join-Path $fixtureDir "openai-chat-live.json")
    "anthropic" = Invoke-LiveProvider -Name "anthropic" -KeyVariable "ANTHROPIC_API_KEY" -OutputFile (Join-Path $fixtureDir "anthropic-live.json")
    "openai-responses" = Invoke-LiveProvider -Name "openai-responses" -KeyVariable "OPENAI_API_KEY" -OutputFile (Join-Path $fixtureDir "openai-responses-live.json")
}
$mcp = Invoke-LiveMcp -OutputFile (Join-Path $fixtureDir "mcp-live.json")

$status = [ordered]@{
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    note = "Live fixtures are written only when the real provider call succeeds. Skips are recorded as pending, never fabricated."
    providers = $providers
    mcp = $mcp
}
[IO.File]::WriteAllText($statusPath, ($status | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding $false))
Write-Host "status: $statusPath"

$failed = @($providers.Values | Where-Object { $_.status -eq "failed" })
if ($mcp.status -eq "failed") {
    $failed += $mcp
}
if ($failed.Count -gt 0) {
    exit 1
}
$pending = @($providers.Values | Where-Object { $_.status -eq "pending" })
if ($pending.Count -eq $providers.Count -and $mcp.status -eq "pending") {
    Write-Host "Live smoke is pending: set OPENAI_API_KEY and/or ANTHROPIC_API_KEY, configure an MCP server in ~/.kl/mcp.toml, then rerun this script."
}
