# Start the actual portable executable and verify that it opens the sample book.
[CmdletBinding()]
param([string]$PublishDirectory = 'release/QuietRead-Windows-x64')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$exePath = (Resolve-Path (Join-Path $PublishDirectory 'QuietRead.exe')).Path
$samplePath = (Resolve-Path (Join-Path $PublishDirectory 'samples/QuietRead-Guide.epub')).Path
$process = $null
$forwardProcess = $null
$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ('QuietRead-published-' + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($temporary) | Out-Null
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
    $forwardPath = Join-Path $temporary 'forward.epub'
    $archive = [System.IO.Compression.ZipFile]::Open($forwardPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @{
            'mimetype' = 'application/epub+zip'
            'META-INF/container.xml' = '<container><rootfiles><rootfile full-path="book.opf"/></rootfiles></container>'
            'book.opf' = '<package><metadata><title>QuietRead 转交检查</title></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>'
            'c.xhtml' = '<html><body><p>第二次启动转交到现有窗口。</p></body></html>'
        }
        foreach ($name in $entries.Keys) {
            $stream = $archive.CreateEntry($name).Open()
            try { $bytes = [System.Text.Encoding]::UTF8.GetBytes($entries[$name]); $stream.Write($bytes, 0, $bytes.Length) }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    $start.Arguments = '"' + $forwardPath + '"'
    $forwardProcess = [System.Diagnostics.Process]::Start($start)
    if (-not $forwardProcess.WaitForExit(15000)) { throw 'Second portable instance did not forward and exit.' }
    if ($forwardProcess.ExitCode -ne 0) { throw "Second portable instance returned $($forwardProcess.ExitCode)." }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $forwarded = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Original portable instance exited during forwarding.' }
        if ($process.MainWindowTitle.Contains('QuietRead 转交检查')) { $forwarded = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $forwarded) { throw 'Original portable instance did not display the forwarded book.' }
    if (-not $process.CloseMainWindow()) { throw 'Portable executable did not accept a graceful close.' }
    if (-not $process.WaitForExit(10000)) { throw 'Portable executable did not close within 10 seconds.' }
    if ($process.ExitCode -ne 0) { throw "Portable executable returned $($process.ExitCode)." }
    Write-Host 'PASS: actual portable QuietRead.exe opened its sample, accepted a second instance and exited cleanly.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
    if ($null -ne $forwardProcess) {
        if (-not $forwardProcess.HasExited) { $forwardProcess.Kill(); $forwardProcess.WaitForExit(5000) | Out-Null }
        $forwardProcess.Dispose()
    }
    if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
}
