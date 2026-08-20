[CmdletBinding()]
param(
    [string]$DriveLetter = 'Z',
    [string]$RemotePath = '\\192.168.219.102\video'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$systemRoot = Join-Path $projectRoot 'runtime\system'
$dataRoot = Join-Path $projectRoot 'runtime\data'
$jellyfinExe = Join-Path $systemRoot 'jellyfin.exe'
$driveName = $DriveLetter.TrimEnd(':') + ':'

if (-not (Test-Path -LiteralPath $jellyfinExe)) {
    throw "Jellyfin executable not found: $jellyfinExe"
}

$mapping = Get-SmbMapping -LocalPath $driveName -ErrorAction SilentlyContinue
if ($mapping -and $mapping.RemotePath -ne $RemotePath) {
    throw "$driveName is already mapped to $($mapping.RemotePath), expected $RemotePath."
}

if ($mapping -and -not (Test-Path -LiteralPath ($driveName + '\'))) {
    Write-Host "Removing disconnected mapping for $driveName before reconnecting it."
    & net.exe use $driveName /delete /yes
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to remove the disconnected mapping for $driveName."
    }

    $mapping = $null
}

if (-not $mapping) {
    Write-Host "Mapping $driveName to $RemotePath for the service session."
    & net.exe use $driveName $RemotePath /persistent:no
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to map $driveName to $RemotePath. Verify the saved NAS credential for the service account."
    }

    Start-Sleep -Seconds 2
}

if (-not (Test-Path -LiteralPath ($driveName + '\'))) {
    throw "Mapped media drive is not accessible: $driveName"
}

& $jellyfinExe `
    --service `
    --datadir $dataRoot `
    --configdir (Join-Path $dataRoot 'config') `
    --cachedir (Join-Path $dataRoot 'cache') `
    --logdir (Join-Path $dataRoot 'log') `
    --webdir (Join-Path $systemRoot 'jellyfin-web') `
    --ffmpeg (Join-Path $systemRoot 'ffmpeg.exe')

exit $LASTEXITCODE
