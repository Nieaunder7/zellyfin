[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ProjectRoot)

$ErrorActionPreference = 'Stop'
$projectRootFull = [System.IO.Path]::GetFullPath($ProjectRoot)
$sourceRoot = Split-Path -Parent $PSScriptRoot
$targetScripts = Join-Path $projectRootFull 'scripts'
$targetScriptsFull = [System.IO.Path]::GetFullPath($targetScripts)
if (-not $targetScriptsFull.StartsWith($projectRootFull + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe destination path.' }

foreach ($file in @('scene-analysis-worker.py', 'scene-translation-worker.py', 'run-scene-analysis.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $targetScripts $file) -Force
}

Write-Host 'Installed scene analysis and translation workers. Their schedules are managed in the Zellyfin dashboard.'
