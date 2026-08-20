[CmdletBinding()]
param(
    [string]$ServiceName = 'Zellyfin',
    [string]$ServiceAccount = "$env:USERDOMAIN\$env:USERNAME",
    [string]$DriveLetter = 'Z',
    [string]$RemotePath = '\\192.168.219.102\video'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $projectRoot 'runtime'
$dataRoot = Join-Path $runtimeRoot 'data'
$launcherPath = Join-Path $PSScriptRoot 'run-jellyfin-service.ps1'
$jellyfinExe = Join-Path $runtimeRoot 'system\jellyfin.exe'
$serviceLog = Join-Path $dataRoot 'log\service-wrapper.log'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Zellyfin Server.lnk'
$disabledShortcut = $startupShortcut + '.disabled'
$nssmRoot = Join-Path $runtimeRoot 'tools\nssm'
$nssmExe = Join-Path $nssmRoot 'nssm.exe'
$nssmUrl = 'https://www.nssm.cc/ci/nssm-2.24-101-g897c7ad.zip'
$nssmArchiveSha1 = 'CA2F6782A05AF85FACF9B620E047B01271EDD11D'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell window (Run as administrator).'
    }
}

function Invoke-Nssm {
    param(
        [string[]]$NssmArguments,
        [switch]$Sensitive
    )

    & $nssmExe @NssmArguments
    if ($LASTEXITCODE -ne 0) {
        $operation = if ($Sensitive) { '[sensitive arguments redacted]' } else { $NssmArguments -join ' ' }
        throw "NSSM failed with exit code ${LASTEXITCODE}: $operation"
    }
}

function Install-Nssm {
    if (Test-Path -LiteralPath $nssmExe) {
        return
    }

    New-Item -ItemType Directory -Path $nssmRoot -Force | Out-Null
    $archivePath = Join-Path $nssmRoot 'nssm.zip'
    $extractPath = Join-Path $nssmRoot 'extract'

    Invoke-WebRequest -Uri $nssmUrl -OutFile $archivePath -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA1).Hash
    if ($actualHash -ne $nssmArchiveSha1) {
        throw "NSSM archive hash mismatch. Expected $nssmArchiveSha1, received $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $downloadedExe = Get-ChildItem -LiteralPath $extractPath -Recurse -Filter 'nssm.exe' |
        Where-Object { $_.FullName -match '[\\/]win64[\\/]nssm\.exe$' } |
        Select-Object -First 1
    if (-not $downloadedExe) {
        throw 'The downloaded NSSM archive did not contain the x64 executable.'
    }

    Copy-Item -LiteralPath $downloadedExe.FullName -Destination $nssmExe -Force
    Remove-Item -LiteralPath $archivePath -Force
    Remove-Item -LiteralPath $extractPath -Recurse -Force
}

Assert-Administrator

if (-not (Test-Path -LiteralPath $jellyfinExe)) {
    throw "Jellyfin executable not found: $jellyfinExe"
}

if (-not (Test-Path -LiteralPath $RemotePath)) {
    throw "NAS path is not accessible in the current account: $RemotePath"
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "A Windows service named $ServiceName already exists."
}

Install-Nssm
$credential = Get-Credential -UserName $ServiceAccount -Message 'Enter the Windows account password used to run the Zellyfin service. A Windows Hello PIN cannot be used.'
if (-not $credential) {
    throw 'Service account credentials were not supplied.'
}

$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential.Password)
$password = $null
$shortcutDisabledByThisRun = $false

try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    $powershellExe = (Get-Command powershell.exe).Source
    $launcherArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`" -DriveLetter `"$DriveLetter`" -RemotePath `"$RemotePath`""

    Invoke-Nssm -NssmArguments @('install', $ServiceName, $powershellExe)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppParameters', $launcherArguments)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppDirectory', $projectRoot)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'DisplayName', 'Zellyfin Server')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'Description', 'Custom Jellyfin 10.11.11 server on port 8097')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'Start', 'SERVICE_AUTO_START')
    Invoke-Nssm -Sensitive -NssmArguments @('set', $ServiceName, 'ObjectName', $credential.UserName, $password)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'DependOnService', 'Tcpip', 'LanmanWorkstation')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppExit', 'Default', 'Restart')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppRestartDelay', '10000')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppStdout', $serviceLog)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppStderr', $serviceLog)
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppRotateFiles', '1')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppRotateOnline', '1')
    Invoke-Nssm -NssmArguments @('set', $ServiceName, 'AppRotateBytes', '10485760')
    & sc.exe config $ServiceName start= delayed-auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to configure delayed automatic startup.' }

    $running = Get-Process -Name jellyfin -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $jellyfinExe } |
        Select-Object -First 1
    if ($running) {
        Stop-Process -Id $running.Id
        $running.WaitForExit(15000) | Out-Null
    }

    if (Test-Path -LiteralPath $startupShortcut) {
        if (Test-Path -LiteralPath $disabledShortcut) {
            throw "Cannot disable startup shortcut because this backup already exists: $disabledShortcut"
        }

        Move-Item -LiteralPath $startupShortcut -Destination $disabledShortcut
        $shortcutDisabledByThisRun = $true
    }

    Start-Service -Name $ServiceName
    $deadline = (Get-Date).AddSeconds(60)
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
        throw "The service started but Jellyfin did not answer on port 8097. Check $serviceLog."
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    Write-Host "Zellyfin service installed successfully."
    Write-Host "State: $($service.State); Start mode: $($service.StartMode); Account: $($service.StartName)"
    Write-Host 'Jellyfin: http://localhost:8097'
} catch {
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        if ($existingService.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        }

        & $nssmExe remove $ServiceName confirm | Out-Null
    }

    if ($shortcutDisabledByThisRun -and (Test-Path -LiteralPath $disabledShortcut)) {
        Move-Item -LiteralPath $disabledShortcut -Destination $startupShortcut
    }

    throw
} finally {
    $password = $null
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
}
