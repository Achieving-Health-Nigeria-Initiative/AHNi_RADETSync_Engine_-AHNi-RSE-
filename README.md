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
- Both write the facility's `FacilityHeartbeat` row (`<DatimId>`/`latest`) with Merge:
  - the heartbeat writes liveness (`LastSeen`, `Status`, `LastReportError`, identity)
  - a successful RADET upload immediately writes `LastReportRun` (Azure's upload time, ISO string),
    `LastReportRows` and `LastReportUrl`. A cycle that uploads nothing sets `Status` to `error`
    and leaves the previous upload's values in place. A restart never blanks them.

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

### 5) Force backfill (RADET for past dates)

Superadmins request backfills in BayCentral (Administration → Force Backfill). BayCentral writes one row
per facility per as-of date to the `ForceBackfillJobs` table (same storage account as the heartbeat):
PartitionKey = DatimId, RowKey = `<RequestId>_<yyyy-MM-dd>`. A date range is one row per day.

- A third loop checks this facility's `pending` rows every `ForceBackfill:PollMinutes` (default 5),
  oldest request first, and runs them one at a time.
- A job is claimed by setting it `running` with an ETag-conditional update; if BayCentral cancelled it
  or another PC with the same DatimId claimed it first, Azure refuses (412) and the job is skipped.
- The run is the normal RADET workbook with the as-of date as "today": every sheet's `:end_date` is that
  date, start dates are the defaults (HTS: start of that fiscal year minus 3 months), and any
  `StartDate`/`EndDate` in `Reports:Scripts` is ignored. It uploads to `RADET/<date>/<facility>_<date>.xlsx`,
  replacing that day's file.
- The job row gets `uploaded` (with Azure's `UploadedAt`, `Rows`, `BlobPath`) or `failed` (with `Error`).
  A backfill never changes the heartbeat row (Last RADET, Status).
- It runs alongside a scheduled cycle, except a backfill for today's date (same file) or when
  `ForceBackfill:RunAlongsideSchedule` is false; then it takes the cycle lock, and a scheduled cycle that
  comes due waits for it instead of being skipped.
- Jobs this PC left `running` when the service stopped go back to `pending` at the next start, or to
  `failed` after `ForceBackfill:MaxAttempts` (default 3) runs.

## Report Output

`RadetReportScript` generates one workbook with these sheets:

| Sheet | Reports:Scripts entry | SQL blob (`autoreport` container) |
|---|---|---|
| 1. RADET (typed mapping) | `RADET` | `report-script/radet.sql` |
| 2. HTS (typed mapping) | `HTS` | `report-script/hts.sql` |
| 3. Index | `Index` | `report-script/index.sql` |
| 4. PMTCT HTS | `PMTCTHTS` | `report-script/pmtct_hts.sql` |
| 5. Maternal Cohort | `Maternal` | `report-script/maternal.sql` |
| 6. Prep | `Prep` | `report-script/prep.sql` |
| 7. AHD | `AHD` | `report-script/ahd.sql` |

SQL scripts:

- Every script is downloaded at the start of each cycle from the `report-script/` folder of the `autoreport`
  blob container (`Storage:Azure:ScriptContainerName`), next to the `RADET/` folder the workbooks are uploaded
  to. Updating a blob updates every facility on its next run. The scripts are the LAMISPlus report module
  2.7.3 queries, unchanged:

  | Sheet | Script | Report module source | Default start |
  |---|---|---|---|
  | RADET | `radet.sql` | `ReportRepository.getRadetData` (+ facility-name CASE) | 1980-01-01 |
  | HTS | `hts.sql` | `ReportRepository.getHtsReport` | 1 July before the end date's fiscal year |
  | Index | `index.sql` | `ReportRepository.getFamilyIndexReport` (no dates) | - |
  | PMTCT HTS | `pmtct_hts.sql` | `ReportRepository.getPmtctReportMetaData` (Pepfar) | 1980-01-01 |
  | Maternal Cohort | `maternal.sql` | `query.yml` `pmtct-maternal-cohort-query` | 1980-01-01 |
  | Prep | `prep.sql` | `query.yml` `prep-query` | 1985-01-01 |
  | AHD | `ahd.sql` | `query.yml` `ahd-query` | 1985-01-01 |

  `query.yml` is the one the report module runs with (`#Released: 2026-09-18`). End dates default to today.
- `BlobPath` may include a folder; if that path is missing, the file name at the container root is tried.
- If the RADET script cannot be loaded the cycle fails and nothing is uploaded. If a sheet 2-7 script
  cannot be loaded or run, that sheet keeps its headers with no rows and the workbook is still uploaded.
- `Enabled: false` on a sheet entry leaves that sheet empty without running its query.

Query parameters, bound as `$1..$5`. `radet.sql` uses the named placeholders; the other scripts use the report
module's positional ones unchanged - bare in its native queries (`?1`), quoted in its `query.yml` queries
(`'?2'`, which the module substitutes as text; bound, the quotes are dropped):

| Named | Positional | Value |
|---|---|---|
| `:facility_id` | `?1` | `FacilityId`, or 0 = `SELECT current_organisation_unit_id FROM base_application_user WHERE id = 1` |
| `:start_date` | `?2` | `StartDate`, or the script default from the end date: RADET 1980-01-01; HTS 1 July before the fiscal year (1 Oct - 30 Sep) containing the end date (EndDate 2025-09-30 -> 2024-07-01) |
| `:end_date` | `?3` | `EndDate`, or today (WAT) |
| `:previous_quarter_end` | `?4` | end of the calendar quarter before the end date's quarter |
| `:previous_previous_quarter_end` | `?5` | end of the quarter before that |

The HTS sheet keeps the 29 columns it had with the earlier blob `hts.sql` (same headers and order); the
report module's other HTS columns, including names and phone numbers, are read but not written.

The Index sheet keeps the 18 headers of the earlier blob `index_report.sql` (`IndexMapper`), each filled from
the column the report module writes under the same header in its HTS_INDEX_REPORT (Relationship to Index,
Notification Method and Date of HTS for the three with slightly different names). Age and Sex of the index
client, Type of contact and HIV Test Status are not in that report and stay empty.

PMTCT HTS and Maternal Cohort keep the headers of the earlier blob scripts too (`SheetLayouts`,
`SheetProjection`); datimCode is the facility's DATIM id. PMTCT HTS takes the columns the report module's PMTCT
HTS (Pepfar) export uses (Setting, Point of Entry, Syphilis Test Result, Previously Known HIV+ Result, Syphilis
Treatment & Referral Result for the differently named ones); Gestational Age and Facility Enrolled In are not in
it and stay empty. Maternal Cohort takes the same-named columns, plus the module's "Mother''s Status<TAB>Timing
of ART initiation in mother", "Birth Outcome" and "Age at CTX (Cotrimoxazole)" columns for the three renamed
headers. Prep and AHD headers are exactly the module's.

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
- `ForceBackfill:Enabled`, `ForceBackfill:PollMinutes`, `ForceBackfill:RunAlongsideSchedule`, `ForceBackfill:MaxAttempts`

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



