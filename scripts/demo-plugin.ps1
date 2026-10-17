$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

cargo build --workspace
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
dotnet build kl.sln
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

$target = (Resolve-Path (Join-Path $root "target")).Path
$base = Join-Path $target "plugin-demo"
if (-not $base.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing to operate outside target/: $base"
}
if (Test-Path -LiteralPath $base) {
    Remove-Item -LiteralPath $base -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $base "workspace\src"),(Join-Path $base "home") -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $base "workspace\src\lib.rs"), "pub fn value() -> u8 { 1 }`n", (New-Object System.Text.UTF8Encoding $false))

$config = @'
[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
'@
[IO.File]::WriteAllText((Join-Path $base "kl.toml"), $config, (New-Object System.Text.UTF8Encoding $false))

$script = @'
[
  {"tool_calls":[{"id":"plugin-call","name":"fake_tool","arguments":{}}]},
  {"text":"plugin tool executed"}
]
'@
[IO.File]::WriteAllText((Join-Path $base "script.json"), $script, (New-Object System.Text.UTF8Encoding $false))

function New-PluginSource {
    param([string]$Version, [string]$ConfigVersion)
    $source = Join-Path $base "source-$Version\fake-tool"
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    $manifest = [ordered]@{
        id = "fake-tool"
        version = $Version
        api = 1
        kind = "tool"
        entrypoint = (Join-Path $target "debug\fake_plugin.exe")
        runtime = "sidecar"
        capabilities = @("tool")
        permissions = [ordered]@{
            network = @("example.com")
            env = @()
            fs_read = @("workspace")
            fs_write = @()
        }
        tool_name = "fake_tool"
        tool_parameters = [ordered]@{ type = "object"; properties = [ordered]@{}; additionalProperties = $false }
        config_path = "config.json"
        health_timeout_ms = 3000
        drain_timeout_ms = 1000
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $source "manifest.json"), ($manifest | ConvertTo-Json -Depth 10), $utf8)
    [IO.File]::WriteAllText((Join-Path $source "config.json"), (@{ version = $ConfigVersion } | ConvertTo-Json), $utf8)
    return $source
}

function New-ProviderSource {
    param([string]$Version, [string]$Text)
    $source = Join-Path $base "provider-$Version\fake-provider"
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    $manifest = [ordered]@{
        id = "fake-provider"
        version = $Version
        api = 1
        kind = "provider"
        entrypoint = (Join-Path $target "debug\fake_plugin.exe")
        runtime = "sidecar"
        capabilities = @("chat", "streaming")
        permissions = [ordered]@{
            network = @()
            env = @()
            fs_read = @()
            fs_write = @()
        }
        models = @("fake-model")
        config_path = "config.json"
        health_timeout_ms = 3000
        drain_timeout_ms = 1000
    }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $source "manifest.json"), ($manifest | ConvertTo-Json -Depth 10), $utf8)
    [IO.File]::WriteAllText((Join-Path $source "config.json"), (@{ provider_text = $Text } | ConvertTo-Json), $utf8)
    return $source
}

$kl = Join-Path $root "cli\Kl.Cli\bin\Debug\net10.0\kl.exe"
$klHome = Join-Path $base "home"
$configPath = Join-Path $base "kl.toml"
$workspace = Join-Path $base "workspace"

$v1 = New-PluginSource -Version "1.0.0" -ConfigVersion "1"
$v2 = New-PluginSource -Version "2.0.0" -ConfigVersion "2"

Write-Host "== install v1 =="
& $kl plugin install $v1 --config $configPath --home $klHome
if ($LASTEXITCODE -ne 0) { throw "plugin install v1 failed" }

Write-Host "== run v1 tool =="
$run1 = & $kl run "use the plugin tool" --workspace $workspace --config $configPath --home $klHome --yes
$run1 | Write-Host
if ($LASTEXITCODE -ne 0 -or ($run1 -join "`n") -notmatch "fake tool 1 ok") {
    throw "v1 plugin tool did not execute"
}

Write-Host "== hot upgrade to v2 =="
& $kl plugin install $v2 --config $configPath --home $klHome
if ($LASTEXITCODE -ne 0) { throw "plugin install v2 failed" }

Write-Host "== run v2 tool =="
$run2 = & $kl run "use the upgraded plugin tool" --workspace $workspace --config $configPath --home $klHome --yes
$run2 | Write-Host
if ($LASTEXITCODE -ne 0 -or ($run2 -join "`n") -notmatch "fake tool 2 ok") {
    throw "v2 plugin tool did not execute"
}

Write-Host "== install provider plugin =="
$provider = New-ProviderSource -Version "1.0.0" -Text "fake provider plugin response"
& $kl plugin install $provider --config $configPath --home $klHome
if ($LASTEXITCODE -ne 0) { throw "provider plugin install failed" }

Write-Host "== run provider plugin =="
$providerRun = & $kl run "say hello" --provider fake-provider --model fake-model --workspace $workspace --config $configPath --home $klHome --yes
$providerRun | Write-Host
if ($LASTEXITCODE -ne 0 -or ($providerRun -join "`n") -notmatch "fake provider plugin response") {
    throw "provider plugin did not execute"
}

Write-Host "== plugin list =="
& $kl plugin list --config $configPath --home $klHome
if ($LASTEXITCODE -ne 0) { throw "plugin list failed" }

Write-Host "== audit =="
& $kl audit 20 --config $configPath --home $klHome
if ($LASTEXITCODE -ne 0) { throw "audit failed" }

Write-Host "Plugin demo passed: $base"
