[CmdletBinding()]
param(
    [switch]$SkipStop,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $projectRoot 'src\Jellyfin.Plugin.StoryPreview\Jellyfin.Plugin.StoryPreview.csproj'
$outputRoot = Join-Path $projectRoot 'artifacts\story-preview'
$installRoot = Join-Path $projectRoot 'runtime\data\plugins\StoryPreview'
$pluginDll = Join-Path $outputRoot 'Jellyfin.Plugin.StoryPreview.dll'
$jellyfinExe = Join-Path $projectRoot 'runtime\system\jellyfin.exe'

dotnet publish $pluginProject --configuration Release --output $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Story Preview plugin build failed.' }
if ($BuildOnly) { return }

if (-not $SkipStop) {
    $running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $jellyfinExe } | Select-Object -First 1
    if ($running) {
        Stop-Process -Id $running.Id
        $running.WaitForExit(10000) | Out-Null
    }
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $installRoot 'Jellyfin.Plugin.StoryPreview.dll') -Force
Write-Host "Installed Story Preview plugin: $installRoot"
