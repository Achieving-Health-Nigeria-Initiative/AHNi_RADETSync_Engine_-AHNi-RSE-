# AHNi RADETSync Engine (AHNi-RSE)

AHNi RADETSync Engine (AHNi-RSE) is a .NET Windows Worker Service that:

1. Runs scheduled report cycles (cron-based)
2. Queries LAMISPlus PostgreSQL
3. Builds Excel workbooks
4. Uploads reports to Azure Blob
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
  - HTS, Index, PMTCT HTS, Maternal, Prep, AHD
- RADET query remains fatal anchor query.
- Non-fatal query failure produces empty sheet, not full cycle failure.

## Report Output

`RadetReportScript` generates one workbook with these sheets:

| Sheet | Reports:Scripts entry | SQL blob (`radet-sql` container) |
|---|---|---|
| 1. RADET (typed mapping) | `RADET` | `radet_query_new.sql` |
| 2. HTS (typed mapping) | `HTS` | `hts.sql` |
| 3. Index (raw DataTable) | `Index` | `index_report.sql` |
| 4. PMTCT HTS (raw DataTable) | `PMTCTHTS` | `pmtct_hts_report.sql` |
| 5. Maternal Cohort (raw DataTable) | `Maternal` | `maternal_cohort_report.sql` |
| 6. Prep (raw DataTable) | `Prep` | `prep_query.sql` |
| 7. AHD (raw DataTable) | `AHD` | `ahd_query_raw.sql` |

SQL scripts:

- A script entry with a `LocalPath` is read from `Reports:LocalScriptFolder` (relative paths resolve against
  the app's content root). RADET and HTS currently do this: they are the LAMISPlus report module 2.7.3
  queries (`ReportRepository.getRadetData` / `getHtsReport`) kept in `RADETSyncEngine\report-script\`.
- Every other script is downloaded from the `Storage:Azure:ScriptContainerName` blob container at the start
  of each cycle, so updating a blob updates every facility on its next run.
- `BlobPath` may include a folder; if that path is missing, the file name at the container root is tried.
- If the RADET script cannot be loaded the cycle fails and nothing is uploaded. If a sheet 2-7 script
  cannot be loaded or run, that sheet keeps its headers with no rows and the workbook is still uploaded.
- `Enabled: false` on a sheet entry leaves that sheet empty without running its query.

Query parameters, bound as `$1..$5`. `radet.sql` uses the named placeholders; `hts.sql` is the report
module's query unchanged and uses its positional `?1..?3`:

| Named | Positional | Value |
|---|---|---|
| `:facility_id` | `?1` | `FacilityId`, or 0 = `SELECT current_organisation_unit_id FROM base_application_user WHERE id = 1` |
| `:start_date` | `?2` | `StartDate`, or the script default from the end date: RADET 1980-01-01; HTS 1 July before the fiscal year (1 Oct - 30 Sep) containing the end date (EndDate 2025-09-30 -> 2024-07-01) |
| `:end_date` | `?3` | `EndDate`, or today (WAT) |
| `:previous_quarter_end` | `?4` | end of the calendar quarter before the end date's quarter |
| `:previous_previous_quarter_end` | `?5` | end of the quarter before that |

The HTS sheet keeps the 29 columns it had with the earlier blob `hts.sql` (same headers and order); the
report module's other HTS columns, including names and phone numbers, are read but not written.

As in the report module, any RADET status containing "stop" is reported as "Stopped Treatment".

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

- `Storage:Azure:ContainerName` (workbook uploads)
- `Storage:Azure:ScriptContainerName` (SQL scripts)
- `Reports:Scripts` (sheet SQL blob paths and on/off switches)
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
- Check the SQL script blob paths in `Reports:Scripts` exist in the script container

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



