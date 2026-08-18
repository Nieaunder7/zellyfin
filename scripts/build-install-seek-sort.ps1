[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$webSource = Join-Path $projectRoot 'build\jellyfin-web-10.11.11'
$webDist = Join-Path $webSource 'dist'
$webPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-seek-sort-infinite-scroll.patch'
$mediaInfoPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-inline-media-info.patch'
$webInstall = Join-Path $projectRoot 'runtime\system\jellyfin-web'
$backupRoot = Join-Path $projectRoot 'runtime\backups'
$jellyfinExe = Join-Path $projectRoot 'runtime\system\jellyfin.exe'

foreach ($path in @($webSource, $webInstall, $backupRoot)) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    if (-not $fullPath.StartsWith($projectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the project: $fullPath"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $webSource '.git'))) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $webSource) -Force | Out-Null
    git clone --depth 1 --branch v10.11.11 https://github.com/jellyfin/jellyfin-web.git $webSource
    if ($LASTEXITCODE -ne 0) { throw 'Unable to clone Jellyfin Web 10.11.11.' }
}

$listController = Join-Path $webSource 'src\controllers\list.js'
if (-not (Select-String -LiteralPath $listController -Pattern 'infiniteScrollSentinel' -Quiet)) {
    git -C $webSource apply $webPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the Seek sort and infinite scroll web patch.' }
}

$itemDetailsTemplate = Join-Path $webSource 'src\controllers\itemDetails\index.html'
if (-not (Select-String -LiteralPath $itemDetailsTemplate -Pattern 'inlineMediaInfoSection' -Quiet)) {
    git -C $webSource apply $mediaInfoPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the inline media info web patch.' }
}

if (-not $SkipBuild) {
    & npm.cmd ci --prefix $webSource
    if ($LASTEXITCODE -ne 0) { throw 'Jellyfin Web dependency installation failed.' }
    & npm.cmd run build:production --prefix $webSource
    if ($LASTEXITCODE -ne 0) { throw 'Jellyfin Web build failed.' }
}

if (-not (Test-Path -LiteralPath (Join-Path $webDist 'index.html'))) {
    throw "Built web client not found: $webDist"
}

& (Join-Path $PSScriptRoot 'build-install-seek-statistics.ps1')

$running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $jellyfinExe } |
    Select-Object -First 1
if ($running) {
    Stop-Process -Id $running.Id
    $running.WaitForExit(10000) | Out-Null
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$backupPath = Join-Path $backupRoot ("jellyfin-web-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (Test-Path -LiteralPath $webInstall) {
    Move-Item -LiteralPath $webInstall -Destination $backupPath
}

try {
    New-Item -ItemType Directory -Path $webInstall -Force | Out-Null
    Copy-Item -Path (Join-Path $webDist '*') -Destination $webInstall -Recurse -Force
} catch {
    if (Test-Path -LiteralPath $webInstall) {
        Remove-Item -LiteralPath $webInstall -Recurse -Force
    }

    if (Test-Path -LiteralPath $backupPath) {
        Move-Item -LiteralPath $backupPath -Destination $webInstall
    }

    throw
}

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

Write-Host "Installed custom Jellyfin Web with Seek count sorting: $webInstall"
Write-Host "Previous web client backup: $backupPath"
