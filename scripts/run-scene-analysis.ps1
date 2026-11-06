[CmdletBinding()]
param(
    [int]$MaxItems = 300,
    [int]$Samples = 24,
    [int]$TimeLimitSeconds = 7200
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workerRoot = Join-Path $projectRoot 'runtime\worker\scene-analysis'
$python = Join-Path $workerRoot '.venv\Scripts\python.exe'
$worker = Join-Path $PSScriptRoot 'scene-analysis-worker.py'
$database = Join-Path $projectRoot 'runtime\data\data\jellyfin.db'
$ffmpeg = Join-Path $projectRoot 'runtime\system\ffmpeg.exe'
$output = Join-Path $projectRoot 'runtime\data\data\scene-analysis'
$priorities = Join-Path $workerRoot 'priority-items.txt'
$log = Join-Path $workerRoot 'batch-scheduled.log'

foreach ($path in @($python, $worker, $database, $ffmpeg)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Scene analysis dependency missing: $path" }
}
New-Item -ItemType Directory -Path $workerRoot -Force | Out-Null
$arguments = @('--library-db', $database, '--ffmpeg', $ffmpeg, '--output', $output,
               '--samples', $Samples, '--max-items', $MaxItems,
               '--time-limit-seconds', $TimeLimitSeconds)
if (Test-Path -LiteralPath $priorities) { $arguments += @('--priority-ids', $priorities) }

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue' # Windows PowerShell wraps native stderr as an ErrorRecord.
try {
    & $python $worker @arguments 2>&1 | Tee-Object -FilePath $log -Append
    $workerExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
if ($workerExitCode -ne 0) { throw "Scene analysis exited with code $workerExitCode" }
