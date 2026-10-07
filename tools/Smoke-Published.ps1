# Start the actual portable executable and verify that it opens the sample book.
[CmdletBinding()]
param([string]$PublishDirectory = 'release/QuietRead-Windows-x64')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exePath = (Resolve-Path (Join-Path $PublishDirectory 'QuietRead.exe')).Path
$samplePath = (Resolve-Path (Join-Path $PublishDirectory 'samples/QuietRead-Guide.epub')).Path
$process = $null
try {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $exePath
    $start.UseShellExecute = $false
    $start.Arguments = '"' + $samplePath + '"'
    $process = [System.Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $opened = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "Portable executable exited before opening its book: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.Responding -and
            $process.MainWindowTitle.Contains('静读 · 阅读指南')) {
            $opened = $true
            break
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $opened) { throw 'Portable executable did not display the sample within 20 seconds.' }
    if (-not $process.CloseMainWindow()) { throw 'Portable executable did not accept a graceful close.' }
    if (-not $process.WaitForExit(10000)) { throw 'Portable executable did not close within 10 seconds.' }
    if ($process.ExitCode -ne 0) { throw "Portable executable returned $($process.ExitCode)." }
    Write-Host 'PASS: actual portable QuietRead.exe opened its sample and exited cleanly.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
