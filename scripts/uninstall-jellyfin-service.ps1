[CmdletBinding()]
param(
    [string]$ServiceName = 'Zellyfin',
    [switch]$RestoreStartupShortcut,
    [switch]$StartInteractive
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]$identity
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as administrator).'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$nssmExe = Join-Path $projectRoot 'runtime\tools\nssm\nssm.exe'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Zellyfin Server.lnk'
$disabledShortcut = $startupShortcut + '.disabled'
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if (-not $service) {
    throw "Windows service not found: $ServiceName"
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

if (Test-Path -LiteralPath $nssmExe) {
    & $nssmExe remove $ServiceName confirm
    if ($LASTEXITCODE -ne 0) { throw 'NSSM could not remove the service.' }
} else {
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Windows could not remove the service.' }
}

if ($RestoreStartupShortcut -and (Test-Path -LiteralPath $disabledShortcut)) {
    if (Test-Path -LiteralPath $startupShortcut) {
        throw "Startup shortcut already exists: $startupShortcut"
    }

    Move-Item -LiteralPath $disabledShortcut -Destination $startupShortcut
}

if ($StartInteractive) {
    Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
        '-File', (Join-Path $PSScriptRoot 'start-jellyfin.ps1')) -WindowStyle Hidden
}

Write-Host 'Zellyfin service removed. Runtime data and media files were not deleted.'
