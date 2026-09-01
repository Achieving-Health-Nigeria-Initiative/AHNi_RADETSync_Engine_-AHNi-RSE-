# publish-linux.ps1 — run from PowerShell on Windows to build and push to MON-01
param(
    [string]$MonIP   = "10.10.60.11",
    [string]$MonUser = "ace1admin",
    [string]$MonDest = "/opt/ahni-rse"
)

$ProjectPath = "$PSScriptRoot\..\AHNiRSE.csproj"
$OutputPath  = "$env:TEMP\AHNiRSE-linux"

Write-Host "=== Step 1: Clean output folder ===" -ForegroundColor Cyan
if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }
New-Item -ItemType Directory -Path $OutputPath | Out-Null

Write-Host "=== Step 2: Publish self-contained Linux binary ===" -ForegroundColor Cyan
dotnet publish $ProjectPath `
    -c Release `
    -r linux-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutputPath

if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed"; exit 1 }

Write-Host "=== Step 3: Copy deploy scripts and config ===" -ForegroundColor Cyan
Copy-Item "$PSScriptRoot\deploy.sh"                           "$OutputPath\"
Copy-Item "$PSScriptRoot\ahni-rse.service"                    "$OutputPath\"
Copy-Item "$PSScriptRoot\..\appsettings.json"                 "$OutputPath\"

# Include Production secrets if present (never committed to git)
$prodConfig = "$PSScriptRoot\..\appsettings.Production.json"
if (Test-Path $prodConfig) {
    Copy-Item $prodConfig "$OutputPath\"
    Write-Host "  Production config included." -ForegroundColor Yellow
} else {
    Write-Warning "appsettings.Production.json not found -- must be placed manually on MON-01 at /opt/ahni-rse/"
}

Write-Host "=== Step 4: Upload to MON-01 ===" -ForegroundColor Cyan
ssh "${MonUser}@${MonIP}" "mkdir -p /tmp/ahni-rse-deploy"
scp -r "$OutputPath\*" "${MonUser}@${MonIP}:/tmp/ahni-rse-deploy/"

Write-Host "=== Step 5: Run deploy on MON-01 ===" -ForegroundColor Cyan
ssh "${MonUser}@${MonIP}" "bash /tmp/ahni-rse-deploy/deploy.sh"

Write-Host "=== Done ===" -ForegroundColor Green
