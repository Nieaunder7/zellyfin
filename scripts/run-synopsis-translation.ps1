[CmdletBinding()]
param(
    [int]$MaxItems = 0,
    [int]$MaxSeconds = 72000,
    [double]$DelaySeconds = 2.5,
    [string]$RuntimeProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$PythonExe = 'python'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath($RuntimeProjectRoot)
$indexDb = Join-Path $projectRoot 'runtime\data\data\synopsis-index.db'
$logDir = Join-Path $projectRoot 'runtime\worker\synopsis-translation'
$logPath = Join-Path $logDir 'translation.log'
$python = (Get-Command $PythonExe -ErrorAction Stop).Source
$worker = Join-Path $PSScriptRoot 'translate-fanza-synopsis.py'

foreach ($path in @($indexDb, $python, $worker)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing dependency: $path" }
}
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
"$(Get-Date -Format o) Starting synopsis translation; max_items=$MaxItems max_seconds=$MaxSeconds delay=$DelaySeconds" |
    Add-Content -LiteralPath $logPath -Encoding UTF8
$env:PYTHONIOENCODING = 'utf-8'
$workerArgs = @($worker, '--index-db', $indexDb, '--delay', "$DelaySeconds",
                '--max-items', "$MaxItems", '--max-seconds', "$MaxSeconds")
& $python @workerArgs 2>&1 | ForEach-Object {
    $line = [string]$_
    Write-Output $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}
$workerExit = $LASTEXITCODE
"$(Get-Date -Format o) Worker exit code: $workerExit" |
    Add-Content -LiteralPath $logPath -Encoding UTF8
exit $workerExit
