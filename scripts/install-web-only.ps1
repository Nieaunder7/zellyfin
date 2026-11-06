[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeProjectRoot,
    [Parameter(Mandatory)][string]$WebDist
)
$ErrorActionPreference = 'Stop'
$runtime = [IO.Path]::GetFullPath((Join-Path $RuntimeProjectRoot 'runtime')).TrimEnd('\')
$source = [IO.Path]::GetFullPath($WebDist).TrimEnd('\')
$installed = Join-Path $runtime 'system\jellyfin-web'
$backup = Join-Path $runtime ('backups\web-only-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (-not (Test-Path -LiteralPath (Join-Path $source 'index.html'))) { throw 'Build output missing.' }
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath $installed -Destination (Join-Path $backup 'jellyfin-web') -Recurse
try {
    # Retain old assets for tabs which still reference the previous build.
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/')
        if ($relative -eq 'index.html') { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $installed $relative))
        if (-not $target.StartsWith($installed + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid output path.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    Copy-Item -LiteralPath (Join-Path $source 'index.html') -Destination (Join-Path $installed 'index.html') -Force
    'Deployed web client without restarting or scanning the server.'
    "Backup: $backup"
} catch {
    Copy-Item -Path (Join-Path $backup 'jellyfin-web\*') -Destination $installed -Recurse -Force
    throw
}
