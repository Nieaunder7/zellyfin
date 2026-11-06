[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$webSource = Join-Path $projectRoot 'build\jellyfin-web-10.11.11'
$webDist = Join-Path $webSource 'dist'
$webPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-seek-sort-infinite-scroll.patch'
$mediaInfoPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-inline-media-info.patch'
$folderControlsPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-folder-collection-controls.patch'
$fileSizePatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-file-size-sort.patch'
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

$listTemplate = Join-Path $webSource 'src\controllers\list.html'
if (-not (Select-String -LiteralPath $listTemplate -Pattern 'btnHomeVideosFolders' -Quiet)) {
    git -C $webSource apply $folderControlsPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the folder controls patch.' }
}
if (-not (Select-String -LiteralPath $listController -Pattern 'const FILE_SIZE_SORT' -Quiet)) {
    git -C $webSource apply $fileSizePatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply the file size sorting patch.' }
}

$verticalScrollPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-vertical-favorites-search.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\pagedItems.js'))) {
    git -C $webSource apply $verticalScrollPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply vertical favorites/search scrolling.' }
}

$playerFilePatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-player-file-actions.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\playerFileActions.js'))) {
    git -C $webSource apply --ignore-space-change $playerFilePatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply player file size and deletion controls.' }
}

$inlineFiltersPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-inline-filters-page-size.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\listPageSize.js'))) {
    git -C $webSource apply --ignore-space-change $inlineFiltersPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply inline filters and page size controls.' }
}

$playFromStartPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-play-from-start.patch'
$playbackManagerSource = Join-Path $webSource 'src\components\playback\playbackmanager.js'
if (-not (Select-String -LiteralPath $playbackManagerSource -Pattern 'Keep saved progress for display' -Quiet)) {
    git -C $webSource apply --ignore-space-change $playFromStartPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply play-from-start behavior.' }
}

$preserveScrollPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-preserve-scroll.patch'
$itemsContainerSource = Join-Path $webSource 'src\elements\emby-itemscontainer\emby-itemscontainer.js'
if (-not (Select-String -LiteralPath $itemsContainerSource -Pattern 'const appendItems = result.AppendItems' -Quiet)) {
    git -C $webSource apply --ignore-space-change $preserveScrollPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply scroll-preserving pagination.' }
}

$storyPreviewPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-story-previews.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\trickplayPreviews.js'))) {
    git -C $webSource apply --ignore-space-change $storyPreviewPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply scene previews.' }
}

$sceneAnalysisPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-scene-analysis-ui.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\sceneAnalysis.js'))) {
    git -C $webSource apply --ignore-space-change $sceneAnalysisPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply scene analysis UI.' }
}

$sceneSearchPositionPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-scene-search-position.patch'
if (-not (Test-Path -LiteralPath (Join-Path $webSource 'src\scripts\sceneSearchPosition.js'))) {
    git -C $webSource apply --ignore-space-change $sceneSearchPositionPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply scene search positions.' }
}

$unifiedSearchPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-unified-search.patch'
$searchResultsSource = Join-Path $webSource 'src\apps\stable\features\search\components\SearchResults.tsx'
if (-not (Select-String -LiteralPath $searchResultsSource -Pattern 'UnifiedVideoSearch' -Quiet)) {
    git -C $webSource apply --ignore-space-change $unifiedSearchPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply unified file and scene search.' }
}

$performerSearchPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-performer-search.patch'
if (-not (Select-String -LiteralPath $searchResultsSource -Pattern 'matchedPerformerCode' -Quiet)) {
    git -C $webSource apply --ignore-space-change $performerSearchPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply performer catalog search results.' }
}

$searchModesPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-search-modes.patch'
if (-not (Select-String -LiteralPath $searchResultsSource -Pattern 'RESULT_TITLES' -Quiet)) {
    git -C $webSource apply --ignore-space-change $searchModesPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply selectable search modes.' }
}

$synopsisSearchPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-synopsis-search.patch'
$synopsisDetailsSource = Join-Path $webSource 'src\scripts\synopsisDetails.js'
if (-not (Test-Path -LiteralPath $synopsisDetailsSource)) {
    git -C $webSource apply --ignore-space-change $synopsisSearchPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply synopsis keyword search and detail UI.' }
}

$keywordResultImagePatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-keyword-result-image.patch'
if (-not (Select-String -LiteralPath $searchResultsSource -Pattern 'sceneAnalysisCover' -Quiet)) {
    git -C $webSource apply --ignore-space-change $keywordResultImagePatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply keyword result cover images.' }
}

$scenePreviewPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-scene-previews.patch'
$previewSource = Join-Path $webSource 'src\scripts\trickplayPreviews.js'
if (-not (Select-String -LiteralPath $previewSource -Pattern 'getScenePreviewFrames' -Quiet)) {
    git -C $webSource apply --ignore-space-change $scenePreviewPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply scene-analysis preview images.' }
}

$inlineStoryPreviewPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-inline-story-previews.patch'
$inlineStoryPreviewSource = Join-Path $webSource 'src\scripts\inlineStoryPreviews.js'
if (-not (Test-Path -LiteralPath $inlineStoryPreviewSource)) {
    git -C $webSource apply --ignore-space-change $inlineStoryPreviewPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply inline story previews.' }
}

$cardStoryCarouselPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-card-story-carousel.patch'
$cardStoryCarouselSource = Join-Path $webSource 'src\scripts\cardStoryCarousel.js'
if (-not (Test-Path -LiteralPath $cardStoryCarouselSource)) {
    git -C $webSource apply --ignore-space-change $cardStoryCarouselPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply card story carousel.' }
}

$autoCardCarouselPatch = Join-Path $projectRoot 'patches\jellyfin-web-10.11.11-auto-card-carousel.patch'
if (-not (Select-String -LiteralPath $cardStoryCarouselSource -Pattern 'playbackObserver' -Quiet)) {
    git -C $webSource apply --ignore-space-change $autoCardCarouselPatch
    if ($LASTEXITCODE -ne 0) { throw 'Unable to apply automatic card story carousel.' }
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

$zellyfinService = Get-Service -Name 'Zellyfin' -ErrorAction SilentlyContinue
$serviceWasRunning = $zellyfinService -and $zellyfinService.Status -eq 'Running'
if ($serviceWasRunning) {
    sc.exe stop Zellyfin | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Service -Name 'Zellyfin').Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
    }
    if ((Get-Service -Name 'Zellyfin').Status -ne 'Stopped') {
        throw 'Zellyfin did not stop within 30 seconds. Resolve the service state before installing.'
    }
}

$running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $jellyfinExe } |
    Select-Object -First 1
if ($running) {
    Stop-Process -Id $running.Id
    $running.WaitForExit(10000) | Out-Null
}

& (Join-Path $PSScriptRoot 'build-install-seek-statistics.ps1') -SkipStop
& (Join-Path $PSScriptRoot 'build-install-story-preview.ps1') -SkipStop

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
if ($serviceWasRunning) {
    Start-Service -Name 'Zellyfin'
} elseif (Test-Path -LiteralPath $startupShortcut) {
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
