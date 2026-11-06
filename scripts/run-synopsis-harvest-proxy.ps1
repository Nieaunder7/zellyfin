[CmdletBinding()]
param(
    [int]$MaxItems = 0,
    [int]$MaxSeconds = 43200,
    [double]$DelaySeconds = 2.5,
    [string]$RuntimeProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PythonExe = 'python'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($RuntimeProjectRoot)
$jellyfinDb = Join-Path $projectRoot 'runtime\data\data\jellyfin.db'
$outputDb = Join-Path $projectRoot 'runtime\data\data\synopsis-index.db'
$logDir = Join-Path $projectRoot 'runtime\worker\synopsis-harvest'
$logPath = Join-Path $logDir 'harvest-proxy.log'
$failuresCsv = Join-Path $logDir 'failures.csv'
$python = (Get-Command $PythonExe -ErrorAction Stop).Source
$worker = Join-Path $PSScriptRoot 'harvest-fanza-synopsis.py'

foreach ($path in @($jellyfinDb, $python, $worker)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing dependency: $path" }
}
if ([string]::IsNullOrWhiteSpace($env:FANZA_PROXY_URL)) {
    throw 'FANZA_PROXY_URL must name a working Japan-based HTTP(S) or SOCKS5H proxy.'
}

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$env:PYTHONIOENCODING = 'utf-8'
$commonArgs = @($worker, '--jellyfin-db', $jellyfinDb, '--output-db', $outputDb)

# The same worker and proxy session perform the probe. A failed route never
# starts the batch, and this launcher never changes the computer-wide VPN.
& $python @commonArgs --probe-route
if ($LASTEXITCODE -ne 0) { throw 'FANZA proxy route probe failed; harvest was not started.' }

"$(Get-Date -Format o) Starting proxy-only synopsis harvest; max_items=$MaxItems max_seconds=$MaxSeconds delay=$DelaySeconds" |
    Add-Content -LiteralPath $logPath -Encoding UTF8
$workerArgs = @($commonArgs, '--failures-csv', $failuresCsv,
                '--delay', "$DelaySeconds", '--max-items', "$MaxItems",
                '--max-seconds', "$MaxSeconds")
& $python @workerArgs 2>&1 | ForEach-Object {
    $line = [string]$_
    Write-Output $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}
$workerExit = $LASTEXITCODE
"$(Get-Date -Format o) Worker exit code: $workerExit" |
    Add-Content -LiteralPath $logPath -Encoding UTF8
exit $workerExit
