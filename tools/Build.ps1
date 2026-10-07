# Build the Windows x64 portable application. Run from Windows PowerShell 5.1 or PowerShell 7.
[CmdletBinding()]
param([switch]$SkipTests, [string]$Tag)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the Microsoft .NET 10 SDK before building this project.'
    }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw 'Install Python 3.11 or newer to package and verify the portable ZIP.'
    }
    if (-not $SkipTests) {
        dotnet run --project tests/QuietRead.Tests/QuietRead.Tests.csproj -c Release -- --benchmark --validate samples/QuietRead-Guide.epub --report docs/core-tests.local.json
        if ($LASTEXITCODE -ne 0) { throw 'Core and security tests failed.' }
    }
    if (Test-Path release/QuietRead-Windows-x64) { Remove-Item release/QuietRead-Windows-x64 -Recurse -Force }
    dotnet publish src/QuietRead.App/QuietRead.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:RestoreLockedMode=true -o release/QuietRead-Windows-x64
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    $packageArgs = @('tools/package_release.py', '--publish', 'release/QuietRead-Windows-x64', '--output', 'artifacts')
    if ($Tag) { $packageArgs += @('--tag', $Tag) }
    python @packageArgs
    if ($LASTEXITCODE -ne 0) { throw 'Portable package verification failed.' }
    Write-Host 'Ready: artifacts/QuietRead-*-Windows-x64.zip'
}
finally { Pop-Location }
