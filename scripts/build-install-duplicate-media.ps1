[CmdletBinding()]
param([switch]$RestartJellyfin)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $projectRoot 'src\Jellyfin.Plugin.DuplicateMedia\Jellyfin.Plugin.DuplicateMedia.csproj'
$outputRoot = Join-Path $projectRoot 'artifacts\duplicate-media'
$installRoot = Join-Path $projectRoot 'runtime\data\plugins\DuplicateMedia'
$pluginDll = Join-Path $outputRoot 'Jellyfin.Plugin.DuplicateMedia.dll'
$jellyfinExe = Join-Path $projectRoot 'runtime\system\jellyfin.exe'

dotnet publish $pluginProject --configuration Release --output $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Duplicate Media plugin build failed.' }
if (-not (Test-Path -LiteralPath $pluginDll)) { throw "Plugin DLL not found: $pluginDll" }

$running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $jellyfinExe } |
    Select-Object -First 1
if ($running) {
    Stop-Process -Id $running.Id
    $running.WaitForExit(10000) | Out-Null
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $installRoot 'Jellyfin.Plugin.DuplicateMedia.dll') -Force
Write-Host "Installed Duplicate Media plugin: $installRoot"

if ($RestartJellyfin) {
    $startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Zellyfin Server.lnk'
    if (Test-Path -LiteralPath $startupShortcut) {
        $shell = New-Object -ComObject Shell.Application
        try {
            $shell.ShellExecute($startupShortcut, '', $projectRoot, 'open', 7)
        } finally {
            [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    } else {
        Start-Process -FilePath 'powershell.exe' -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
            '-File', (Join-Path $PSScriptRoot 'start-jellyfin.ps1')) -WindowStyle Hidden
    }

    Write-Host 'Jellyfin restart requested.'
}
