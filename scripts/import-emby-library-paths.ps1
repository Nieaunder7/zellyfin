[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:8097',
    [switch]$Apply,
    [switch]$FirstRun,
    [switch]$RefreshLibrary
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $projectRoot 'migration\emby-library-paths.json'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Migration manifest not found: $manifestPath"
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$missingPaths = @()

Write-Host "Source: $($manifest.source)"
Write-Host "Target: $($manifest.target)"
Write-Host ''

foreach ($library in $manifest.libraries) {
    Write-Host "Library: $($library.name) [$($library.collectionType)]"
    foreach ($mediaPath in $library.paths) {
        $exists = Test-Path -LiteralPath $mediaPath -PathType Container
        Write-Host "  [$($exists ? 'OK' : 'MISSING')] $mediaPath"
        if (-not $exists) {
            $missingPaths += $mediaPath
        }
    }
}

if ($missingPaths.Count -gt 0) {
    throw "One or more source media paths are unavailable. No changes were made."
}

if (-not $Apply) {
    Write-Host ''
    Write-Host 'Dry run complete. No Jellyfin settings were changed.'
    Write-Host 'Before completing the setup wizard, use -Apply -FirstRun.'
    Write-Host 'After completing the setup wizard, use -Apply and enter administrator credentials.'
    exit 0
}

$passwordPointer = [IntPtr]::Zero
$password = $null

try {
    $normalizedBaseUrl = $BaseUrl.TrimEnd('/')
    $headers = @{}

    if ($FirstRun) {
        Write-Host 'Using Jellyfin first-run setup authorization.'
    }
    else {
        $username = Read-Host 'Jellyfin administrator username'
        if ([string]::IsNullOrWhiteSpace($username)) {
            throw 'The Jellyfin administrator username cannot be empty.'
        }

        $securePassword = Read-Host 'Jellyfin administrator password' -AsSecureString
        $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
        $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
        $clientAuthorization = 'MediaBrowser Client="Zellyfin Library Migration", Device="Windows", DeviceId="zellyfin-library-import", Version="1.0.0"'
        $loginBody = @{
            Username = $username
            Pw = $password
        } | ConvertTo-Json
        $loginResponse = Invoke-RestMethod -Method Post -Uri "$normalizedBaseUrl/Users/AuthenticateByName" -Headers @{ Authorization = $clientAuthorization } -ContentType 'application/json' -Body $loginBody
        if ([string]::IsNullOrWhiteSpace($loginResponse.AccessToken)) {
            throw 'Jellyfin authentication did not return an access token.'
        }

        $headers = @{ Authorization = "MediaBrowser Token=`"$($loginResponse.AccessToken)`"" }
    }

    $existingLibraries = @(Invoke-RestMethod -Method Get -Uri "$normalizedBaseUrl/Library/VirtualFolders" -Headers $headers)

    foreach ($library in $manifest.libraries) {
        $existingLibrary = $existingLibraries | Where-Object { $_.Name -eq $library.name } | Select-Object -First 1

        # During first-run setup the API can temporarily return an unnamed aggregate root.
        # Use the local library options as an idempotency fallback for this portable instance.
        if ($FirstRun -and -not $existingLibrary) {
            $localOptionsPath = Join-Path $projectRoot "runtime\data\root\default\$($library.name)\options.xml"
            if (Test-Path -LiteralPath $localOptionsPath) {
                [xml]$localOptions = Get-Content -Raw -LiteralPath $localOptionsPath
                $localPaths = @($localOptions.LibraryOptions.PathInfos.MediaPathInfo | ForEach-Object { $_.Path })
                $existingLibrary = [pscustomobject]@{
                    Name = $library.name
                    Locations = $localPaths
                }
            }
        }

        if (-not $existingLibrary) {
            $encodedName = [Uri]::EscapeDataString([string]$library.name)
            $encodedType = [Uri]::EscapeDataString([string]$library.collectionType)
            $encodedPaths = [Uri]::EscapeDataString(($library.paths -join ','))
            $uri = "$normalizedBaseUrl/Library/VirtualFolders?name=$encodedName&collectionType=$encodedType&paths=$encodedPaths&refreshLibrary=false"
            Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' -Body '{}'
            Write-Host "[CREATED] $($library.name)"
            continue
        }

        $existingPaths = @($existingLibrary.Locations)
        foreach ($mediaPath in $library.paths) {
            if ($existingPaths -contains $mediaPath) {
                Write-Host "[EXISTS] $($library.name): $mediaPath"
                continue
            }

            $body = @{
                Name = $library.name
                Path = $mediaPath
            } | ConvertTo-Json
            Invoke-RestMethod -Method Post -Uri "$normalizedBaseUrl/Library/VirtualFolders/Paths?refreshLibrary=false" -Headers $headers -ContentType 'application/json' -Body $body
            Write-Host "[ADDED] $($library.name): $mediaPath"
        }
    }

    if ($RefreshLibrary) {
        Invoke-RestMethod -Method Post -Uri "$normalizedBaseUrl/Library/Refresh" -Headers $headers
        Write-Host '[STARTED] Jellyfin library scan'
    }

    Write-Host 'Library path migration completed.'
}
finally {
    if ($passwordPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
    }
    $password = $null
}
