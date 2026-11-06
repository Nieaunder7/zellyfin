[CmdletBinding()]
param(
    [int]$MaxItems = 0,
    [int]$MaxSeconds = 43200,
    [double]$DelaySeconds = 2.5,
    [string]$RuntimeProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PythonExe = 'python'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($RuntimeProjectRoot)
$jellyfinDb = Join-Path $projectRoot 'runtime\data\data\jellyfin.db'
$outputDb = Join-Path $projectRoot 'runtime\data\data\synopsis-index.db'
$logDir = Join-Path $projectRoot 'runtime\worker\synopsis-harvest'
$logPath = Join-Path $logDir 'harvest.log'
$failuresCsv = Join-Path $logDir 'failures.csv'
$nordvpn = 'C:\Program Files\NordVPN\NordVPN.exe'
$python = (Get-Command $PythonExe -ErrorAction Stop).Source
$worker = Join-Path $PSScriptRoot 'harvest-fanza-synopsis.py'

foreach ($path in @($jellyfinDb, $nordvpn, $python, $worker)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing dependency: $path" }
}
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
"$(Get-Date -Format o) Starting synopsis harvest; max_items=$MaxItems max_seconds=$MaxSeconds delay=$DelaySeconds" |
    Add-Content -LiteralPath $logPath -Encoding UTF8

$disconnectRequired = $false
$workerExit = 1
try {
    # Official Windows CLI: https://support.nordvpn.com/hc/en-us/articles/19919384880145
    $disconnectRequired = $true
    & $nordvpn -c -g Japan

    $probeUrl = 'https://www.dmm.co.jp/mono/dvd/-/search/=/searchstr=RCT-781/'
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 2
        try {
            $response = Invoke-WebRequest -Uri $probeUrl -UseBasicParsing -TimeoutSec 10
            if ($response.Content -notmatch 'このページはお住まいの地域から') {
                $ready = $true
                break
            }
        } catch {
            # The VPN connection can briefly interrupt outbound requests.
        }
    }
    if (-not $ready) { throw 'Japan VPN route did not pass the FANZA access probe.' }
    if (-not (Test-Path 'Z:\') -or -not (Test-Path 'Y:\')) {
        throw 'A NAS drive became unavailable after VPN connection.'
    }

    $env:PYTHONIOENCODING = 'utf-8'
    $workerArgs = @($worker, '--jellyfin-db', $jellyfinDb, '--output-db', $outputDb,
                    '--failures-csv', $failuresCsv,
                    '--delay', "$DelaySeconds", '--max-items', "$MaxItems",
                    '--max-seconds', "$MaxSeconds")
    & $python @workerArgs 2>&1 | ForEach-Object {
        $line = [string]$_
        Write-Output $line
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    }
    $workerExit = $LASTEXITCODE
    "$(Get-Date -Format o) Worker exit code: $workerExit" |
        Add-Content -LiteralPath $logPath -Encoding UTF8
} catch {
    "$(Get-Date -Format o) Wrapper error: $($_.Exception.Message)" |
        Add-Content -LiteralPath $logPath -Encoding UTF8
    throw
} finally {
    if ($disconnectRequired) {
        & $nordvpn -d
        Start-Sleep -Seconds 3
    }
    "$(Get-Date -Format o) VPN disconnect requested; Z=$(Test-Path 'Z:\') Y=$(Test-Path 'Y:\')" |
        Add-Content -LiteralPath $logPath -Encoding UTF8
}
exit $workerExit
