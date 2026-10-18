$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$demo = Join-Path $root 'target/review-demo'
$reviewHome = Join-Path $root 'target/review-home'
foreach ($path in @($demo, $reviewHome)) {
    $resolved = [IO.Path]::GetFullPath($path)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($root), [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to clean outside repository: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $demo | Out-Null
Copy-Item -Path (Join-Path $root 'tests/fixtures/review-repo/base/*') -Destination $demo -Recurse
git -C $demo init | Out-Null
git -C $demo -c user.email=review@example.test -c user.name=review add .
git -C $demo -c user.email=review@example.test -c user.name=review commit -m base | Out-Null
Copy-Item -Path (Join-Path $root 'tests/fixtures/review-repo/changed/*') -Destination $demo -Recurse -Force

cargo build -p kl-core --manifest-path (Join-Path $root 'Cargo.toml') | Out-Null
dotnet build (Join-Path $root 'cli/Kl.Cli/Kl.Cli.csproj') --no-restore | Out-Null
$cli = Join-Path $root 'cli/Kl.Cli/bin/Debug/net10.0/kl.dll'
$reviewArgs = @('review', '--workspace', $demo, '--strict', '--home', $reviewHome)
$output = & dotnet $cli @reviewArgs 2>&1
$exit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }
if ($exit -ne 1) {
    throw "expected review exit 1 for high findings, got $exit"
}
$text = $output -join "`n"
foreach ($expected in @(
    'debug-unwrap',
    'secret-openai',
    'cross-file-command-injection',
    'LOW-CONFIDENCE'
)) {
    if (-not $text.Contains($expected)) {
        throw "review output is missing $expected"
    }
}
Write-Host 'review demo ok'
