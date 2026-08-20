[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipPluginInstall
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$webSource = Join-Path $projectRoot 'build\jellyfin-web-10.11.11'
$webDist = Join-Path $webSource 'dist'
$webPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-seek-sort-infinite-scroll.patch'
$mediaInfoPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-inline-media-info.patch'
$webInstall = Join-Path $projectRoot 'runtime\system\jellyfin-web'
$backupRoot = Join-Path $projectRoot 'runtime\backups'
$stagingRoot = Join-Path $projectRoot 'runtime\staging'
$jellyfinExe = Join-Path $projectRoot 'runtime\system\jellyfin.exe'
$seekPluginDll = Join-Path $projectRoot 'artifacts\seek-statistics\Jellyfin.Plugin.SeekStatistics.dll'
$seekPluginInstall = Join-Path $projectRoot 'runtime\data\plugins\SeekStatistics'

foreach ($path in @($webSource, $webInstall, $backupRoot, $stagingRoot)) {
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

if (-not $SkipPluginInstall) {
    & (Join-Path $PSScriptRoot 'build-install-seek-statistics.ps1') -BuildOnly
}

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$stagingPath = Join-Path $stagingRoot ("jellyfin-web-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
Copy-Item -Path (Join-Path $webDist '*') -Destination $stagingPath -Recurse -Force

$running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $jellyfinExe } |
    Select-Object -First 1
if ($running) {
    Stop-Process -Id $running.Id
    $running.WaitForExit(10000) | Out-Null
}

if (-not $SkipPluginInstall) {
    New-Item -ItemType Directory -Path $seekPluginInstall -Force | Out-Null
    Copy-Item -LiteralPath $seekPluginDll -Destination (Join-Path $seekPluginInstall 'Jellyfin.Plugin.SeekStatistics.dll') -Force
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$backupPath = Join-Path $backupRoot ("jellyfin-web-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))

if (Test-Path -LiteralPath $webInstall) {
    Move-Item -LiteralPath $webInstall -Destination $backupPath
}

try {
    Move-Item -LiteralPath $stagingPath -Destination $webInstall
} catch {
    if (Test-Path -LiteralPath $webInstall) {
        Remove-Item -LiteralPath $webInstall -Recurse -Force
    }

    if (Test-Path -LiteralPath $backupPath) {
        Move-Item -LiteralPath $backupPath -Destination $webInstall
    }

    throw
}

$service = Get-Service -Name 'Zellyfin' -ErrorAction SilentlyContinue
if ($service) {
    $deadline = (Get-Date).AddSeconds(90)
    $healthy = $false
    do {
        Start-Sleep -Seconds 1
        try {
            $response = Invoke-WebRequest -Uri 'http://localhost:8097/System/Info/Public' -UseBasicParsing -TimeoutSec 3
            $healthy = $response.StatusCode -eq 200
        } catch {
            $healthy = $false
        }
    } until ($healthy -or (Get-Date) -ge $deadline)

    if (-not $healthy) {
        throw 'The Zellyfin service did not restore Jellyfin on port 8097 after web deployment.'
    }
} else {
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
}

Write-Host "Installed custom Jellyfin Web with Seek count sorting: $webInstall"
Write-Host "Previous web client backup: $backupPath"
