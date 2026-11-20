##############################################################################
#  Build-Release.ps1 - builds the two facility packages from one build:
#    AHNi-RSE-<version>.zip                    gen-pop facilities        (Facility:IsCommunityCentre = false)
#    AHNi-RSE-<version>-community-centre.zip   community centres (care)  (Facility:IsCommunityCentre = true)
#  They differ only in that setting; Install.bat shows which one is being installed.
#
#  1. Publishes ONE self-contained AHNiRSE.exe for win-x64 (.NET and every library inside it, so
#     facility servers need nothing installed). The package is that exe, appsettings.json, Install.bat
#     and Deploy-AHNi-RSEService.ps1.
#  2. Puts the live values into the package's appsettings.json: the storage and database
#     connection strings (and licence key, if given) from a secrets file kept OUTSIDE the repo.
#     The repo's appsettings.json only has placeholders, and Install.bat copies the package's
#     appsettings.json over the facility's, so the package must carry the real values.
#  3. Refuses to build a package that still has a placeholder, local test settings or test tables.
#  4. Zips both packages; the publish folders are deleted afterwards (they hold the secrets too).
#
#  USAGE (from the repo folder):
#    .\deploy\Build-Release.ps1
#    .\deploy\Build-Release.ps1 -Secrets D:\keys\rse.json -OutDir D:\releases -Version 2026.11.6.1
#
#  Secrets file (default ..\release.secrets.json, next to the repo folder) - see
#  deploy\release.secrets.example.json. Keep it out of git and off shared drives.
#
#  AUTO-UPDATE (-Publish): also uploads the exe to autoreport/rse-release and points manifest.json at it, so
#  facilities already running an RSE with auto-update install it themselves (see Update/UpdateWorker.cs).
#  Only the exe is published; facility settings and secrets never leave this PC.
#    .\deploy\Build-Release.ps1 -Publish -Facilities "DATIM1,DATIM2" -Notes "Maternal layout"   # pilot first
#    .\deploy\Manage-Update.ps1 promote -Facilities all                                         # then everyone
#  Storage access: the secrets file's connection string, or -Account <storage account> to use your own
#  Azure sign-in (needs "Storage Blob Data Contributor"); with -Account and no secrets file only the exe is
#  built and published, without install packages. The git tree must be committed; the commit is recorded.
##############################################################################
param(
    [string]$Secrets = "",
    [string]$OutDir  = "",
    [string]$Version = "",
    [switch]$Publish,
    [string]$Facilities = "",
    [string]$Notes = "",
    [string]$Account = ""
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Secrets) { $Secrets = Join-Path (Split-Path $repo -Parent) 'release.secrets.json' }
if (-not $OutDir)  { $OutDir  = Join-Path (Split-Path $repo -Parent) 'releases' }

# -- Secrets -----------------------------------------------------------------
$haveSecrets = Test-Path $Secrets
if (-not $haveSecrets -and -not ($Publish -and $Account)) {
    throw "Secrets file not found: $Secrets (copy deploy\release.secrets.example.json there and fill it in)."
}
if ($haveSecrets) {
    $s = Get-Content $Secrets -Raw | ConvertFrom-Json
    foreach ($name in 'AzureBlobStorage', 'DatabaseConnectionString') {
        $v = $s.$name
        if (-not $v -or $v -match '\.\.\.' -or $v -match 'REPLACE_WITH') { throw "Secrets file: '$name' is not filled in." }
    }
}

# -- Auto-update publish: checks before building ------------------------------
if ($Publish) {
    if (-not $Facilities) { throw "-Publish needs -Facilities: 'all' or a comma-separated list of DATIM ids (pilot first)." }
    $dirty = & git -C $repo status --porcelain --untracked-files=no
    if ($dirty) { throw "Commit your changes before publishing (the release records the commit):`n$($dirty -join "`n")" }
    $commit = (& git -C $repo rev-parse --short HEAD).Trim()
    $publisher = (& git -C $repo config user.name)
    if (-not $publisher) { $publisher = $env:USERNAME }
}

# Uploads the built exe and points autoreport/rse-release/manifest.json at it (tools/RsePublish).
function Publish-Update([string]$exePath) {
    $toolArgs = @('run', '--project', (Join-Path $repo 'tools\RsePublish'), '-v', 'q', '--',
                  'publish', '--exe', $exePath, '--facilities', $Facilities, '--notes', $Notes,
                  '--commit', $commit, '--by', $publisher)
    if ($Account) { $toolArgs += @('--account', $Account) } else { $toolArgs += @('--secrets', $Secrets) }
    Write-Host ""
    Write-Host "Publishing the update ($Facilities)..." -ForegroundColor Cyan
    & dotnet @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing the update failed ($LASTEXITCODE)." }
}

function JsonString([string]$value) { $value.Replace('\', '\\').Replace('"', '\"') }

# -- Publish -----------------------------------------------------------------
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stage = Join-Path $OutDir 'AHNi-RSE-publish'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$publishArgs = @('publish', (Join-Path $repo 'AHNi-RSE.csproj'), '-c', 'Release', '-r', 'win-x64',
                 '--self-contained', 'true', '-o', $stage, '-nologo', '-v', 'q',
                 '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                 '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')
if ($Version) { $publishArgs += "-p:Version=$Version" }
Write-Host "Publishing one self-contained AHNiRSE.exe (win-x64)..." -ForegroundColor Cyan
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

try {
    if (-not $haveSecrets) {
        # -Publish -Account without a secrets file: the exe only, no install packages.
        $exe = Get-Item (Join-Path $stage 'AHNiRSE.exe')
        $loose = @(Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.dll', '.pdb' })
        if ($loose -or $exe.Length -lt 30MB) { throw "Not a single self-contained AHNiRSE.exe; not published." }
        $copy = Join-Path $OutDir "AHNiRSE-$($exe.VersionInfo.FileVersion).exe"
        Copy-Item $exe.FullName $copy -Force
        Publish-Update $copy
        Write-Host "No secrets file, so no install packages were built (new installs need them)." -ForegroundColor Yellow
        return
    }

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
    $loose = @(Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.dll', '.pdb' -or $_.Name -like '*.runtimeconfig.json' -or $_.Name -like '*.deps.json' })
    if ($loose)                                                  { $problems += "not a single exe - loose files: $($loose.Name -join ', ')" }
    $exe = Get-Item (Join-Path $stage 'AHNiRSE.exe')
    if ($exe.Length -lt 30MB)                                    { $problems += "AHNiRSE.exe is only $([math]::Round($exe.Length / 1MB)) MB - .NET is not bundled in it" }
    if (Get-ChildItem $stage -Recurse -Filter 'release.secrets*.json') { $problems += 'a secrets file got into the package' }
    if ($text -notmatch '"IsCommunityCentre"\s*:\s*false')       { $problems += 'Facility:IsCommunityCentre is not false in the repo appsettings.json (the gen-pop package needs false)' }
    if ($problems) { throw "Package not built:`n  - " + ($problems -join "`n  - ") }

    # -- Community-centre copy: the same files with Facility:IsCommunityCentre = true -------------
    $careStage = Join-Path $OutDir 'AHNi-RSE-publish-community-centre'
    if (Test-Path $careStage) { Remove-Item $careStage -Recurse -Force }
    Copy-Item $stage $careStage -Recurse
    $careSettings = Join-Path $careStage 'appsettings.json'
    $careText = [regex]::Replace($text, '("IsCommunityCentre"\s*:\s*)false', '${1}true')
    [IO.File]::WriteAllText($careSettings, $careText, (New-Object Text.UTF8Encoding($false)))
    if ($careText -notmatch '"IsCommunityCentre"\s*:\s*true') { throw 'Could not set Facility:IsCommunityCentre in the community-centre package.' }

    # -- Zip -----------------------------------------------------------------
    $exeVersion = (Get-Item (Join-Path $stage 'AHNiRSE.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
    $packages = @(
        @{ Stage = $stage;     Zip = Join-Path $OutDir "AHNi-RSE-$exeVersion.zip";                  Kind = 'gen-pop facilities' },
        @{ Stage = $careStage; Zip = Join-Path $OutDir "AHNi-RSE-$exeVersion-community-centre.zip"; Kind = 'community centres' }
    )
    Write-Host ""
    foreach ($p in $packages) {
        if (Test-Path $p.Zip) { Remove-Item $p.Zip -Force }
        Compress-Archive -Path (Join-Path $p.Stage '*') -DestinationPath $p.Zip
        Write-Host "Built $($p.Zip)" -ForegroundColor Green
        Write-Host "  for $($p.Kind)"
    }
    $files = (Get-ChildItem $stage -Recurse -File | ForEach-Object Name) -join ', '
    Write-Host "  version $exeVersion - single self-contained AHNiRSE.exe ($([math]::Round($exe.Length / 1MB)) MB), live settings filled in"
    Write-Host "  contents: $files"
    Write-Host "  Unzip the right package on the facility server and double-click Install.bat."

    if ($Publish) {
        # The bare exe (no settings, no secrets) is what auto-update installs.
        $copy = Join-Path $OutDir "AHNiRSE-$($exe.VersionInfo.FileVersion).exe"
        Copy-Item $exe.FullName $copy -Force
        Publish-Update $copy
    }
}
finally {
    foreach ($dir in @($stage, (Join-Path $OutDir 'AHNi-RSE-publish-community-centre'))) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
}
