# ============================================================
# AHNi-RSE — Upload modified SQL scripts to Azure Blob Storage
# Container : radet-sql   Prefix: scripts/
# Run this once after modifying any SQL script
# ============================================================

$containerName = "radet-sql"
$prefix        = "scripts/"
$connStr       = $env:AZURE_STORAGE_CONNECTION_STRING

if ([string]::IsNullOrWhiteSpace($connStr)) {
    throw "Set AZURE_STORAGE_CONNECTION_STRING before running this script."
}

$scriptDir = Join-Path $PSScriptRoot "..\Database\SqlScripts"

$scripts = @(
    "radet_query.sql",
    "hts_query.sql",
    "index_query.sql",
    "pmtcthts_query.sql",
    "maternal_query.sql"
)

Add-Type -Path (
    Get-ChildItem "$PSScriptRoot\..\bin\Debug\net8.0\Azure.Storage.Blobs.dll" |
    Select-Object -First 1 -ExpandProperty FullName
)
Add-Type -Path (
    Get-ChildItem "$PSScriptRoot\..\bin\Debug\net8.0\Azure.Core.dll" |
    Select-Object -First 1 -ExpandProperty FullName
)

$serviceClient    = [Azure.Storage.Blobs.BlobServiceClient]::new($connStr)
$containerClient  = $serviceClient.GetBlobContainerClient($containerName)

foreach ($script in $scripts) {
    $localPath  = Join-Path $scriptDir $script
    $blobName   = "$prefix$script"

    if (-not (Test-Path $localPath)) {
        Write-Warning "Not found: $localPath — skipping"
        continue
    }

    $blobClient = $containerClient.GetBlobClient($blobName)
    $stream     = [System.IO.File]::OpenRead($localPath)
    try {
        $blobClient.Upload($stream, $true)   # $true = overwrite
        Write-Host "Uploaded: $blobName" -ForegroundColor Green
    } finally {
        $stream.Dispose()
    }
}

Write-Host "`nAll scripts uploaded." -ForegroundColor Cyan
