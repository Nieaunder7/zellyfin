[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectRoot)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$webDist = Join-Path $root 'build\jellyfin-web-10.11.11\dist'
$webInstall = Join-Path $root 'runtime\system\jellyfin-web'
$pluginSource = Join-Path $root 'artifacts\seek-statistics\Jellyfin.Plugin.SeekStatistics.dll'
$pluginInstall = Join-Path $root 'runtime\data\plugins\SeekStatistics\Jellyfin.Plugin.SeekStatistics.dll'
$backupRoot = Join-Path $root 'runtime\backups'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$webStage = Join-Path $root "runtime\system\jellyfin-web-scene-stage-$stamp"
$webBackup = Join-Path $backupRoot "jellyfin-web-before-scene-$stamp"
$pluginBackup = Join-Path $backupRoot "SeekStatistics-before-scene-$stamp.dll"
$server = Join-Path $root 'runtime\system\jellyfin.exe'

foreach ($target in @($webDist, $webInstall, $pluginSource, $pluginInstall, $backupRoot, $webStage, $webBackup, $pluginBackup, $server)) {
    $resolved = [System.IO.Path]::GetFullPath($target)
    if (-not $resolved.StartsWith("$root\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the project: $resolved"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $webDist 'index.html'))) { throw 'Build the web client first.' }
if (-not (Test-Path -LiteralPath $pluginSource)) { throw 'Publish the plugin first.' }

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
New-Item -ItemType Directory -Path $webStage -Force | Out-Null
Copy-Item -Path (Join-Path $webDist '*') -Destination $webStage -Recurse -Force

$running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $server } | Select-Object -First 1
if ($running) {
    Stop-Process -Id $running.Id
    $running.WaitForExit(15000) | Out-Null
    if (-not $running.HasExited) { throw 'Jellyfin did not stop.' }
}

if (Test-Path -LiteralPath $webInstall) { Move-Item -LiteralPath $webInstall -Destination $webBackup }
try {
    Move-Item -LiteralPath $webStage -Destination $webInstall
    if (Test-Path -LiteralPath $pluginInstall) { Copy-Item -LiteralPath $pluginInstall -Destination $pluginBackup }
    Copy-Item -LiteralPath $pluginSource -Destination $pluginInstall -Force
} catch {
    if (Test-Path -LiteralPath $webInstall) { Remove-Item -LiteralPath $webInstall -Recurse -Force }
    if (Test-Path -LiteralPath $webBackup) { Move-Item -LiteralPath $webBackup -Destination $webInstall }
    if (Test-Path -LiteralPath $pluginBackup) { Copy-Item -LiteralPath $pluginBackup -Destination $pluginInstall -Force }
    throw
}

Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', (Join-Path $root 'scripts\start-jellyfin.ps1')) -WindowStyle Hidden
Write-Host "Installed scene-analysis UI and API; web backup: $webBackup; plugin backup: $pluginBackup"
