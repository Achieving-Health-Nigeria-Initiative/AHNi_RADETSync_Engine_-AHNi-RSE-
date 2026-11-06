##############################################################################
#  Build-Release.ps1 - builds the facility package (AHNi-RSE-<version>.zip)
#
#  1. Publishes self-contained for win-x64 (facility servers don't need .NET installed).
#  2. Puts the live values into the package's appsettings.json: the storage and database
#     connection strings (and licence key, if given) from a secrets file kept OUTSIDE the repo.
#     The repo's appsettings.json only has placeholders, and Install.bat copies the package's
#     appsettings.json over the facility's, so the package must carry the real values.
#  3. Refuses to build a package that still has a placeholder, local test settings or test tables.
#  4. Zips it; the publish folder is deleted afterwards (it holds the secrets too).
#
#  USAGE (from the repo folder):
#    .\deploy\Build-Release.ps1
#    .\deploy\Build-Release.ps1 -Secrets D:\keys\rse.json -OutDir D:\releases -Version 2026.11.6.1
#
#  Secrets file (default ..\release.secrets.json, next to the repo folder) - see
#  deploy\release.secrets.example.json. Keep it out of git and off shared drives.
##############################################################################
param(
    [string]$Secrets = "",
    [string]$OutDir  = "",
    [string]$Version = ""
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Secrets) { $Secrets = Join-Path (Split-Path $repo -Parent) 'release.secrets.json' }
if (-not $OutDir)  { $OutDir  = Join-Path (Split-Path $repo -Parent) 'releases' }

# -- Secrets -----------------------------------------------------------------
if (-not (Test-Path $Secrets)) {
    throw "Secrets file not found: $Secrets (copy deploy\release.secrets.example.json there and fill it in)."
}
$s = Get-Content $Secrets -Raw | ConvertFrom-Json
foreach ($name in 'AzureBlobStorage', 'DatabaseConnectionString') {
    $v = $s.$name
    if (-not $v -or $v -match '\.\.\.' -or $v -match 'REPLACE_WITH') { throw "Secrets file: '$name' is not filled in." }
}

function JsonString([string]$value) { $value.Replace('\', '\\').Replace('"', '\"') }

# -- Publish -----------------------------------------------------------------
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stage = Join-Path $OutDir 'AHNi-RSE-publish'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$publishArgs = @('publish', (Join-Path $repo 'AHNi-RSE.csproj'), '-c', 'Release', '-r', 'win-x64',
                 '--self-contained', 'true', '-o', $stage, '-nologo', '-v', 'q')
if ($Version) { $publishArgs += "-p:Version=$Version" }
Write-Host "Publishing (self-contained, win-x64)..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

try {
    # -- Live settings -------------------------------------------------------
    $settingsPath = Join-Path $stage 'appsettings.json'
    $text = [IO.File]::ReadAllText($settingsPath)

    $text = [regex]::Replace($text, '("AzureBlobStorage"\s*:\s*")REPLACE_WITH_REAL_CONNECTION_STRING_ON_DEPLOY_TARGET(")',
        { param($m) $m.Groups[1].Value + (JsonString $s.AzureBlobStorage) + $m.Groups[2].Value })
    $text = [regex]::Replace($text, '("ConnectionString"\s*:\s*")REPLACE_WITH_REAL_CONNECTION_STRING_ON_DEPLOY_TARGET(")',
        { param($m) $m.Groups[1].Value + (JsonString $s.DatabaseConnectionString) + $m.Groups[2].Value })
    if ($s.LicenseKey) {
        $text = $text.Replace('"YOUR_LICENSE_KEY_HERE"', '"' + (JsonString $s.LicenseKey) + '"')
    }
    [IO.File]::WriteAllText($settingsPath, $text, (New-Object Text.UTF8Encoding($false)))

    # -- Checks: nothing left to fill in, nothing from local testing ---------
    $problems = @()
    if ($text -match 'REPLACE_WITH_REAL_CONNECTION_STRING') { $problems += 'a connection string is still a placeholder' }
    if ($text -match '"LocalUploadFolder"\s*:\s*"[^"]+"')  { $problems += 'Storage:Azure:LocalUploadFolder is set (local test mode - nothing would be uploaded)' }
    if ($text -match 'Local"|Test"|LocalHeartbeat')         { $problems += 'a table name looks like a test table' }
    if ($text -notmatch '"TableName"\s*:\s*"FacilityHeartbeat"') { $problems += 'Heartbeat:TableName is not FacilityHeartbeat' }
    if ($text -notmatch '"TableName"\s*:\s*"ForceBackfillJobs"') { $problems += 'ForceBackfill:TableName is not ForceBackfillJobs' }
    if (-not (Test-Path (Join-Path $stage 'coreclr.dll')))       { $problems += 'not self-contained (coreclr.dll missing)' }
    if (Get-ChildItem $stage -Recurse -Filter 'release.secrets*.json') { $problems += 'a secrets file got into the package' }
    if ($problems) { throw "Package not built:`n  - " + ($problems -join "`n  - ") }

    # -- Zip -----------------------------------------------------------------
    $exeVersion = (Get-Item (Join-Path $stage 'AHNiRSE.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
    $zip = Join-Path $OutDir "AHNi-RSE-$exeVersion.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    $files = (Get-ChildItem $stage -Recurse -File).Count
    Write-Host ""
    Write-Host "Built $zip" -ForegroundColor Green
    Write-Host "  version $exeVersion, $files files, self-contained, live settings (storage + database filled in)"
    Write-Host "  Unzip on the facility server and run Install.bat as Administrator."
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
