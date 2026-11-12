# AHNi RADETSync Engine (AHNi-RSE)

AHNi RADETSync Engine (AHNi-RSE) is a .NET Windows Worker Service that:

1. Runs scheduled report cycles (cron-based)
2. Queries LAMISPlus PostgreSQL
3. Builds Excel workbooks
4. Uploads reports to Azure Blob (or local storage)
5. Sends heartbeat status so BayCentral can track facility online/offline state

## Current Runtime Design

### 1) Service supervision and resilience

- `Program.cs` starts the host even if startup Azure blob health check fails (warning only).
- `Worker` runs under a supervisor loop:
  - Initializes schedule/timezone
  - Starts heartbeat loop and report loop
  - If one loop exits unexpectedly, both loops are restarted after a short delay
  - Schedule initialization failures are retried (service does not stop permanently)

### 2) Heartbeat and report loops are independent

- Heartbeat loop always runs at `Heartbeat:IntervalMinutes`
- Report loop runs by cron (`Reports:CronSchedule` / `Reports:CronSchedules`)
- Heartbeat does not wait for report completion

### 3) Cycle safety

- `ReportOrchestrator` uses a cycle lock (`SemaphoreSlim`) to avoid overlapping cycles.
- WAT date is captured once at cycle start and reused everywhere in that cycle.

### 4) Parallel execution

- `ScriptManager` supports bounded parallel script execution using:
  - `Reports:MaxParallelScripts` (default 1)
- `RadetReportScript` runs non-fatal sheet queries in parallel:
  - HTS, Index, PMTCT HTS, Maternal
- RADET query remains fatal anchor query.
- Non-fatal query failure produces empty sheet, not full cycle failure.

## Report Output

`RadetReportScript` generates one workbook with these sheets:

1. RADET (typed mapping)
2. HTS (typed mapping)
3. Index (raw DataTable)
4. PMTCT HTS (raw DataTable)
5. Maternal Cohort (raw DataTable)

Destination pattern:

- Blob path: `RADET/yyyy-MM-dd/<facility>_yyyy_MM_dd.xlsx`

## Auth Behavior

Auth service sends:

```json
{
  "serialNumber": "<bios-serial-number>",
  "datim": "<facility-datim-id>"
}
```

Tier behavior:

- `Auth:LicenseTier=Enterprise`: requires BIOS serial (`wmic bios get serialnumber` / `Win32_BIOS`); no fallback identity.
- `Auth:LicenseTier=Premium`: prefers BIOS serial, then falls back to board serial / machine-guid hash.

Config:

- `Auth:Enabled` is optional.
- If missing:
  - Development environment: auth is disabled
  - Non-development environment: auth is enabled
- If explicitly set, that value is used.

Cache behavior:

- Authorized=true cached for UTC day
- Authorized=false cached for `Auth:BlockedCacheMinutes`

## Configuration Reference

Main file: `appsettings.json`

Important keys:

- `StorageProvider`: `Azure` or `Local`
- `Storage:Azure:ContainerName`
- `Storage:Azure:ScriptContainerName`
- `Storage:Azure:LocalScriptPath`
- `Database:ConnectionString`
- `Reports:CronSchedule` and/or `Reports:CronSchedules`
- `Reports:RunOnStartup`
- `Reports:TimeZoneId` (recommended: `Africa/Lagos`)
- `Reports:GlobalCycleTimeoutSeconds`
- `Reports:DefaultScriptTimeoutSeconds`
- `Reports:MaxParallelScripts`
- `Heartbeat:IntervalMinutes`
- `Heartbeat:OfflineThresholdMinutes`

## Local Development

1. Copy `appsettings.Development.example.json` to `appsettings.Development.json`
2. Fill local secrets
3. Run:

```powershell
dotnet build
dotnet run --project .\AHNi-RSE.csproj
```

## Publish

Example self-contained publish:

```powershell
dotnet publish .\AHNi-RSE.csproj -c Release -r win-x64 --self-contained true -o C:\Temp\AHNi-RSE-publish
```

Current publish behavior:

- `appsettings.json` is included
- `appsettings.Development*.json` is excluded
- SQL scripts under `Scripts\` are included
- Release PDB symbols are disabled

Zip package:

```powershell
Compress-Archive -Path "C:\Temp\AHNi-RSE-publish\*" -DestinationPath "C:\Temp\AHNi-RSE.zip" -Force
```

## Windows Service Deployment

### Option A: Installer batch

```powershell
.\Install.bat
```

### Option B: Direct PowerShell deploy script

```powershell
.\Deploy-AHNi-RSEService.ps1
```

Optional custom path:

```powershell
.\Deploy-AHNi-RSEService.ps1 -InstallPath "D:\Services\AHNi-RSE"
```

Deploy script configures:

- Service create/update
- Automatic delayed startup
- Recovery restart on failures
- Firewall rules
- Defender exclusions

## Reliability During Network Loss

Expected behavior when network is down:

- Service process keeps running
- Startup does not terminate because of blob health failure
- Heartbeat write failures are retried and cooled down safely
- Report failures are logged and next cycles continue normally

## Security Checklist Before Go-Live

1. Do not commit real secrets in tracked files
2. Keep production secrets only on target machine
3. Rotate any secret that was ever committed
4. Verify auth is enabled in production (explicitly or by environment)
5. Confirm firewall and service recovery settings after install

## Troubleshooting

### Service running but no uploads

- Check storage connection string
- Check blob container names
- Check SQL script paths in script container and local fallback

### Auth blocking runs

- Verify DATIM enrollment in backend
- Verify endpoint and license values
- Clear auth cache if needed:

```powershell
Remove-Item "$env:LOCALAPPDATA\AHNi-RSE\auth-cache.json" -ErrorAction SilentlyContinue
```

### No future cron occurrence warning

- Check `Reports:CronSchedule` syntax
- Check `Reports:TimeZoneId`

### Report cycle skipped

- Previous cycle still running (cycle lock)
- Increase interval, optimize queries, or adjust timeouts



