##############################################################################
#  Manage-Update.ps1 - controls the published RSE auto-update release (autoreport/rse-release/manifest.json).
#  Publishing a new version is  .\deploy\Build-Release.ps1 -Publish -Facilities ...
#
#    .\deploy\Manage-Update.ps1 status                          what is published, for whom, paused or not
#    .\deploy\Manage-Update.ps1 promote -Facilities all         from the pilot facilities to everyone
#    .\deploy\Manage-Update.ps1 promote -Facilities "D1,D2"     or to other DATIM ids
#    .\deploy\Manage-Update.ps1 pause                           stop installs right away (already updated stay)
#    .\deploy\Manage-Update.ps1 resume
#    .\deploy\Manage-Update.ps1 rollback                        everyone goes back to the previous release
#
#  Storage access: -Secrets <file> (default ..\release.secrets.json, its AzureBlobStorage connection string)
#  or -Account <storage account> for your own Azure sign-in (needs "Storage Blob Data Contributor").
#  Each facility's heartbeat shows the result: AppVersion, UpdateStatus, UpdateTargetVersion, UpdateError.
##############################################################################
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('status', 'promote', 'pause', 'resume', 'rollback')]
    [string]$Action,
    [string]$Facilities = "",
    [string]$Secrets = "",
    [string]$Account = ""
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Secrets) { $Secrets = Join-Path (Split-Path $repo -Parent) 'release.secrets.json' }
if ($Action -eq 'promote' -and -not $Facilities) { throw "promote needs -Facilities: 'all' or a comma-separated list of DATIM ids." }

$publisher = (& git -C $repo config user.name)
if (-not $publisher) { $publisher = $env:USERNAME }

$toolArgs = @('run', '--project', (Join-Path $repo 'tools\RsePublish'), '-v', 'q', '--', $Action, '--by', $publisher)
if ($Facilities) { $toolArgs += @('--facilities', $Facilities) }
if ($Account) { $toolArgs += @('--account', $Account) } else { $toolArgs += @('--secrets', $Secrets) }

& dotnet @toolArgs
exit $LASTEXITCODE
