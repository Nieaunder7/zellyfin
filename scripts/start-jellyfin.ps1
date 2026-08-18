[CmdletBinding()]
param()

$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $projectRoot 'runtime'
$systemRoot = Join-Path $runtimeRoot 'system'
$dataRoot = Join-Path $runtimeRoot 'data'
$jellyfinExe = Join-Path $systemRoot 'jellyfin.exe'
$ffmpegExe = Join-Path $systemRoot 'ffmpeg.exe'
$webRoot = Join-Path $systemRoot 'jellyfin-web'
$configRoot = Join-Path $dataRoot 'config'
$cacheRoot = Join-Path $dataRoot 'cache'
$logRoot = Join-Path $dataRoot 'log'

if (-not (Test-Path -LiteralPath $jellyfinExe)) {
    throw "Jellyfin executable not found: $jellyfinExe"
}

$listener = Get-NetTCPConnection -LocalPort 8097 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    $processName = if ($process) { $process.ProcessName } else { 'unknown process' }
    throw "Port 8097 is already in use by $processName (PID $($listener.OwningProcess))."
}

@($dataRoot, $configRoot, $cacheRoot, $logRoot, (Join-Path $dataRoot 'plugins')) |
    ForEach-Object { New-Item -ItemType Directory -Path $_ -Force | Out-Null }

Write-Host 'Starting Jellyfin 10.11.11 at http://localhost:8097'
Write-Host 'Press Ctrl+C to stop the test server.'

& $jellyfinExe `
    --datadir $dataRoot `
    --configdir $configRoot `
    --cachedir $cacheRoot `
    --logdir $logRoot `
    --webdir $webRoot `
    --ffmpeg $ffmpegExe

exit $LASTEXITCODE
