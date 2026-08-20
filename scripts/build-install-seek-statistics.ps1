[CmdletBinding()]
param(
    [switch]$RestartJellyfin,
    [switch]$SkipStop,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $projectRoot 'src\Jellyfin.Plugin.SeekStatistics\Jellyfin.Plugin.SeekStatistics.csproj'
$outputRoot = Join-Path $projectRoot 'artifacts\seek-statistics'
$installRoot = Join-Path $projectRoot 'runtime\data\plugins\SeekStatistics'
$pluginDll = Join-Path $outputRoot 'Jellyfin.Plugin.SeekStatistics.dll'
$jellyfinExe = Join-Path $projectRoot 'runtime\system\jellyfin.exe'

dotnet publish $pluginProject --configuration Release --output $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
if (-not (Test-Path -LiteralPath $pluginDll)) { throw "Plugin DLL not found: $pluginDll" }

if ($BuildOnly) {
    Write-Host "Built Seek Statistics plugin: $pluginDll"
    return
}

if (-not $SkipStop) {
    $running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $jellyfinExe } |
        Select-Object -First 1
    if ($running) {
        Stop-Process -Id $running.Id
        $running.WaitForExit(10000) | Out-Null
    }
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $installRoot 'Jellyfin.Plugin.SeekStatistics.dll') -Force

Write-Host "Installed Seek Statistics plugin: $installRoot"
if ($RestartJellyfin) {
    $service = Get-Service -Name 'Zellyfin' -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host 'The Zellyfin service will restart the server process automatically.'
    } else {
        Start-Process -FilePath 'powershell.exe' -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
            '-File', (Join-Path $PSScriptRoot 'start-jellyfin.ps1')) -WindowStyle Hidden
        Write-Host 'Jellyfin restart requested.'
    }
}
