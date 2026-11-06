[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeProjectRoot,
    [Parameter(Mandatory)][string]$WebDist,
    [Parameter(Mandatory)][string]$PluginDll
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RuntimeProjectRoot).TrimEnd('\')
$runtime = Join-Path $root 'runtime'
$backup = Join-Path $runtime ('backups\library-sorting-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$installedWeb = Join-Path $runtime 'system\jellyfin-web'
$stagedWeb = Join-Path $runtime ('staging\sorting-' + [Guid]::NewGuid().ToString('N'))
$installedPlugin = Join-Path $runtime 'data\plugins\SeekStatistics\Jellyfin.Plugin.SeekStatistics.dll'
$expectedExe = Join-Path $runtime 'system\jellyfin.exe'
$expectedWrapper = Join-Path $runtime 'tools\nssm\nssm.exe'
foreach ($target in @($backup, $installedWeb, $stagedWeb, $installedPlugin)) {
    if (-not [IO.Path]::GetFullPath($target).StartsWith($runtime + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Deployment target is outside the runtime.'
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $WebDist 'index.html')) -or -not (Test-Path -LiteralPath $PluginDll)) {
    throw 'Build the web client and plugin before deployment.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this prepared deployment script as administrator to restart the Windows service.'
}
New-Item -ItemType Directory -Path $backup, $stagedWeb -Force | Out-Null
Start-Transcript -Path (Join-Path $backup 'install.log') | Out-Null
$webMoved = $false
try {
    Copy-Item -Path (Join-Path $WebDist '*') -Destination $stagedWeb -Recurse -Force
    Copy-Item -LiteralPath $installedPlugin -Destination (Join-Path $backup 'Jellyfin.Plugin.SeekStatistics.dll')
    $service = Get-Service Zellyfin
    if ($service.Status -eq 'Running') { Stop-Service Zellyfin -NoWait }
    try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    catch {
        $state = Get-CimInstance Win32_Service -Filter "Name='Zellyfin'"
        $wrapper = Get-CimInstance Win32_Process -Filter "ProcessId=$($state.ProcessId)"
        $server = Get-CimInstance Win32_Process -Filter "Name='jellyfin.exe'" |
            Where-Object ExecutablePath -eq $expectedExe
        if ($state.State -ne 'Stop Pending' -or $server -or $wrapper.Name -ne 'nssm.exe' -or $wrapper.ExecutablePath -ne $expectedWrapper) { throw }
        Stop-Process -Id $state.ProcessId -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    Move-Item -LiteralPath $installedWeb -Destination (Join-Path $backup 'jellyfin-web')
    $webMoved = $true
    Move-Item -LiteralPath $stagedWeb -Destination $installedWeb
    Copy-Item -LiteralPath $PluginDll -Destination $installedPlugin -Force
    if ((Get-FileHash $PluginDll).Hash -ne (Get-FileHash $installedPlugin).Hash) { throw 'Plugin hash mismatch.' }
    Start-Service Zellyfin
    $healthy = $false
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        Start-Sleep -Seconds 2
        try { $healthy = (Invoke-WebRequest 'http://127.0.0.1:8097/System/Info/Public' -TimeoutSec 3).StatusCode -eq 200 } catch {}
        if ($healthy) { break }
    }
    if (-not $healthy) { throw 'Server health check failed; backup retained for recovery.' }
    'Installed file size sorting and verified server health.' | Tee-Object -FilePath (Join-Path $backup 'success.txt')
} catch {
    # Keep all new files for diagnosis, and restore the previous client/plugin only while stopped.
    if ((Get-Service Zellyfin).Status -eq 'Stopped' -and $webMoved) {
        if (Test-Path -LiteralPath $installedWeb) { Move-Item -LiteralPath $installedWeb -Destination (Join-Path $backup 'failed-web') }
        Move-Item -LiteralPath (Join-Path $backup 'jellyfin-web') -Destination $installedWeb
        Copy-Item -LiteralPath (Join-Path $backup 'Jellyfin.Plugin.SeekStatistics.dll') -Destination $installedPlugin -Force
        Start-Service Zellyfin
    }
    $_ | Out-String | Set-Content (Join-Path $backup 'failure.txt')
    throw
} finally { Stop-Transcript | Out-Null }
