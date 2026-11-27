# AHNi RADETSync Engine (AHNi-RSE)

AHNi RADETSync Engine (AHNi-RSE) is a .NET Windows Worker Service that:

1. Runs scheduled report cycles (cron-based)
2. Queries LAMISPlus PostgreSQL
3. Builds Excel workbooks
4. Uploads reports to Azure Blob
5. Sends heartbeat status so BayCentral can track facility online/offline state
6. Runs the RADET backfills requested in BayCentral (Force Backfill), as the workbook or as raw
   query results (Parquet) for a separate combiner

## Project Layout

Code is grouped by what the service does; each folder is one namespace (`AHNiRSE.<Folder>`), and
`GlobalUsings.cs` imports them all.

| Folder | What it holds |
|---|---|
| `Program.cs` | Startup: logging, settings, service registration, blob health check |
| `Configuration/` | Every settings class (`Settings.cs`), bound from `appsettings.json` |
| `Reports/` | The RADET workbook: `RadetWorkbook` (build + upload), `RawReport` (raw-type backfills: the same scripts' results as Parquet), `CustomQuery` (custom-type backfills: a Backfill app SQL script, read-only, as Parquet), `ReportOrchestrator` (one scheduled cycle or backfill: registration check, timeout, audit, Last RADET), `ReportScripts` (which blob holds each sheet's SQL), `ReportPeriods` (each sheet's dates), `CycleContext` |
| `Reports/Sheets/` | Row models, mappers and header lists of the seven sheets |
| `Schedule/` | `ScheduleWorker` (startup cycle, then cron cycles) and `CronSchedule` |
| `Backfill/` | `BackfillWorker` and `ForceBackfillJobStore` (the `ForceBackfillJobs` table) |
| `Heartbeat/` | `HeartbeatWorker`, `AzureHeartbeatService` (the `FacilityHeartbeat` row), `FacilityRunState` |
| `Licensing/` | The verify-serial registration check: `InstallationAuthService`, `DeviceIdentity`, `AuthResultCache`, `VerifySerialClient` |
| `Platform/` | Postgres (incl. the read-only runner for custom scripts), `SqlGuard` (checks a custom script and its parameters), blob storage, Excel writer, Parquet writer (`ParquetExport`), WAT clock, memory release, health check, cycle audit, `RestartingWorker` |
| `deploy/` | `Build-Release.ps1`, `Install.bat`, `Deploy-AHNi-RSEService.ps1` (published next to the exe), `Run-Test.bat` (test packages), `Sync-Time.ps1` |
| `Update/` | Auto-update: `UpdateWorker` (checks the manifest, downloads, installs), `UpdateApplier` (swaps the exe, rolls back), `UpdateStatus`, `UpdateManifest` |
| `tools/LocalRun/` | Runs one workbook, raw report or custom script against a local database, saved to a folder, or acts as a facility's RSE for test job tables (see Local Development) |
| `tools/RsePublish/` | Publishes and manages auto-update releases from the command line (`deploy\Build-Release.ps1 -Publish`, `deploy\Manage-Update.ps1`) |

Interfaces exist only where a test or tool swaps the implementation: `IDatabaseService`,
`IStorageProvider`, `IReportClock`.

## Current Runtime Design

### 1) Service supervision and resilience

- `Program.cs` starts the host even if startup Azure blob health check fails (warning only).
- Three independent background services: `HeartbeatWorker`, `ScheduleWorker` and `BackfillWorker`.
  Each derives from `RestartingWorker`, which restarts its loop 15 seconds after it fails or exits,
  so one loop failing never stops the others (invalid cron settings are retried the same way).

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

- `RadetWorkbook` runs the non-fatal sheet queries in parallel with RADET:
  - HTS, Index, PMTCT HTS, Maternal, Prep, AHD
- The workbook runs within the RADET entry's `TimeoutSeconds` (else `Reports:DefaultScriptTimeoutSeconds`).
- RADET query remains fatal anchor query.
- Non-fatal query failure produces empty sheet, not full cycle failure.

### 5) Force backfill (RADET for past dates)

Superadmins request backfills in BayCentral (Administration → Force Backfill). BayCentral writes one row
per facility per as-of date to the `ForceBackfillJobs` table (same storage account as the heartbeat):
PartitionKey = DatimId, RowKey = `<RequestId>_<yyyy-MM-dd>`. A date range is one row per day.

- `BackfillWorker` checks this facility's `pending` rows every `ForceBackfill:PollMinutes` (default 5),
  oldest request first, and runs them one at a time.
- A job is claimed by setting it `running` with an ETag-conditional update; if BayCentral cancelled it
  or another PC with the same DatimId claimed it first, Azure refuses (412) and the job is skipped.
- The run is the normal RADET workbook with the as-of date as "today": every sheet's `:end_date` is that
  date, start dates are the defaults (HTS: start of that fiscal year minus 3 months), and any
  `StartDate`/`EndDate` in `Reports:Scripts` is ignored. It uploads to `<WorkbookFolder>/<date>/<facility>_<datim>_<date>.xlsx`,
  replacing that day's file.
- The job row gets `uploaded` (with Azure's `UploadedAt`, `Rows`, `BlobPath`) or `failed` (with `Error`).
  A backfill never changes the heartbeat row (Last RADET, Status).
- It runs alongside a scheduled cycle, except a backfill for today's date (same file) or when
  `ForceBackfill:RunAlongsideSchedule` is false; then it takes the cycle lock, and a scheduled cycle that
  comes due waits for it instead of being skipped.
- The job's `ReportType` picks what it produces: `default` (or empty) is the workbook above; `raw` is
  the raw report (next section). A type this RSE does not know is marked `failed` without running
  ("not supported by this RSE ... update the RSE"), so an old install never mistakes a new type for the
  workbook. A raw job never takes the cycle lock for today's date, since it writes different files.
- Each date is its own job with its own run limit (`Reports:GlobalCycleTimeoutSeconds`, 1 hour); a date
  range runs as one job per day, one after another.
- Jobs this PC left `running` when the service stopped go back to `pending` at the next start, or to
  `failed` after `ForceBackfill:MaxAttempts` (default 3) runs.
- On every poll, a job still `running` 3 x `Reports:GlobalCycleTimeoutSeconds` (3 hours) after it was
  picked up is released the same way, whichever PC took it: no live run lasts that long, so that PC
  was retired (for example after a database migration), renamed or died, and nothing else would
  free the job.

### 6) Raw report (force backfill, report type `raw`)

Only produced when a superadmin requests a backfill of type `raw` in BayCentral - never by the scheduled
cycles. `RawReport` runs the same seven scripts as the workbook (downloaded from `report-script/` in
blob storage), with the same parameters as a workbook backfill for that date, and uploads each result
exactly as the query returned it - same column names and order, no header mapping, no value changes -
as a Parquet file. A separate combiner downloads them, maps the headers and formats, and merges them.

```
<container>/raw-report/<yyyy-MM-dd>/<base>_RADET.parquet
                                   <base>_HTS.parquet
                                   <base>_Index.parquet
                                   <base>_PMTCT_HTS.parquet
                                   <base>_Maternal_Cohort.parquet
                                   <base>_Prep.parquet
                                   <base>_AHD.parquet
                                   <base>_manifest.json      (uploaded last)
```

- `<base>` and `<container>` are the workbook's: `Numan_General_Hospital_KXoSesiSLeE_2026_11_02` in `autoreport`,
  `Jalingo_Community_Center_cmkm8UQpvWk_2026_11_02` in `community-center` (see Community centres).
- Column types come from the PostgreSQL type of each result column, not from the values: `date` ->
  DATE, `timestamp`/`timestamptz` -> timestamp, `smallint`/`integer`/`bigint` -> int16/32/64,
  `numeric`/`real`/`double precision` -> double, `boolean` -> boolean, everything else (text, uuid,
  time, arrays...) -> string. Every column is nullable; a report with no rows still has all its columns.
- Each file's footer metadata holds `report`, `script`, `facility`, `datim_id`, `state`, `as_of_date`,
  `start_date`, `end_date` and `pg_types` (the PostgreSQL type of every column).
- The manifest lists the facility, type, container, as-of date, job, RSE version and, per report, the
  script, file, rows, columns and period. It goes up after every Parquet file, so a manifest means
  the set is complete.
- All or nothing: if any enabled script fails, nothing is uploaded and the job fails naming the
  report(s) and error. A script with `Enabled=false` is skipped and listed as `enabled: false`.
- The job row gets `Rows` (RADET), `BlobPath` (the manifest) and `Files` (JSON array of every path).
- When reading the files with pandas, text such as `None` (an Education value in LAMISPlus) is data,
  not a missing value; Parquet keeps it as text, but `read_csv`/`read_excel` defaults would not.

### 7) Custom SQL (force backfill, report type `custom`)

A SQL script saved in the Backfill app (Custom SQL tab) runs at the chosen facilities and each facility's result
is uploaded as one Parquet file (`Reports/CustomQuery.cs`):

```
autoreport/report-script-custom/<name>/<sha256>.sql                every saved version (written by the Backfill app)
custom-reports/<script>/<request id>/<Facility>_<DATIM>.parquet    each facility's result (CustomScript:Container)
```

- The job row carries `ScriptName`, `ScriptBlob`, `ScriptSha256`, `ParamStartDate`, `ParamEndDate` and
  `Parameters` (JSON of the script's own values). The RSE refuses a blob outside `report-script-custom/` and a script
  whose SHA-256 differs from the job's, so a facility runs exactly the version that was chosen.
- `Platform/SqlGuard.cs` checks the script: one statement, starting with SELECT or WITH, no INSERT, UPDATE,
  DELETE, MERGE, TRUNCATE, DROP, ALTER, CREATE, GRANT, REVOKE or SELECT INTO, and none of the functions that
  act outside the query (`pg_terminate_backend`, `dblink`, `set_config`, `lo_export`, `pg_read_file`,
  `pg_sleep`, `query_to_xml`, `nextval` ...). Strings and comments are skipped. The Backfill app runs the same
  checks (`backfill_app/sqlguard.py`) when a script is saved.
- It then runs in a `READ ONLY` transaction that is always rolled back, so PostgreSQL itself refuses any write,
  with `statement_timeout` = `CustomScript:TimeoutSeconds` (900) and at most `CustomScript:MaxRows` (2,000,000)
  rows; more fails the job.
- Parameters: `:facility_id` `?1`, `:start_date` `?2`, `:end_date` `?3`, `:previous_quarter_end` `?4`,
  `:previous_previous_quarter_end` `?5` (also quoted, `'?2'`), plus any other `:name`, whose value is set
  when the run is requested and sent untyped, so PostgreSQL types it from where it is used (text otherwise;
  cast for another type, e.g. `:min_age::int`).
- It runs alongside scheduled cycles like a raw report, and the Parquet footer records the script, its
  SHA-256, the facility, the dates and every parameter value.
- RSEs older than this release fail `custom` jobs without running anything ("not supported by this RSE").
  Facilities without a `CustomScript` section use the defaults above.

### 8) Auto-update

The service updates its own `AHNiRSE.exe` from blob storage (`Update/UpdateWorker.cs`). Facilities that
were installed before auto-update need one manual install of a release that has it; after that, new releases
install themselves. Only the exe is replaced, never `appsettings.json`, so every new setting needs a working
default in code.

```
autoreport/rse-release/manifest.json             the release facilities should run
                       AHNiRSE-<version>.exe     each published exe (never replaced or deleted)
```

- Every `AutoUpdate:CheckMinutes` (5) the service reads the manifest. It installs when the manifest's
  `version` is newer than its own (or `rollback` is true and it is different), `facilities` lists its DATIM id
  or `all`, and `paused` is false. Versions are compared as numbers (`yyyy.M.d.Hmm`, the build time).
- It waits a random 0-`SpreadMinutes` (10) so facilities don't all download at once, downloads the exe to
  `<install>\update\` and refuses it unless its size, SHA-256 and file version match the manifest.
- It installs only when no report cycle or force backfill is running and no scheduled cycle is due within
  `QuietMinutesBeforeSchedule` (10), and holds the cycle lock so none starts meanwhile.
- A copy of the current exe (`update\AHNiRSE-updater.exe --apply-update ...`, `Update/UpdateApplier.cs`) stops
  the service, renames `AHNiRSE.exe` to `AHNiRSE.previous.exe`, copies the new exe in and starts the service.
  The new version marks itself healthy after running for a minute; if that doesn't happen within
  `HealthCheckMinutes` (5) the updater puts the previous exe back, starts it, and that version is never
  tried again on that server (`update\failed-versions.txt`). Its log is `logs\update-<date>.log`.
- The heartbeat row carries `DateStyleVersion`, the build's date-style version (`yyyy.M.d.Hmm`, e.g.
  `2026.11.24.1657`), and `PackageType` (`live`). Older RSE builds report `AppVersion` 1.0.0.0 and no
  `DateStyleVersion`, so a facility whose row has no `DateStyleVersion` is still on an old RSE. A test package
  writes `TestAppVersion`, `TestDateStyleVersion` and `TestLastSeen` instead, so running one beside the installed
  service never makes that facility look updated.
- The heartbeat row adds `UpdateStatus` (UpToDate, NotInRollout, Paused, Waiting, Downloading,
  WaitingForIdle, Installing, Installed, RolledBack, Failed, CheckFailed, Disabled), `UpdateTargetVersion`,
  `UpdateError`, `UpdateLastChecked` and `UpdateLastInstalled` next to `AppVersion`.
- `AutoUpdate:Enabled=false` in a facility's `appsettings.json` switches it off there.

Publishing: in the Backfill app's **RSE release** tab (choose the exe, pick pilot facilities or all, publish;
then change who installs it, pause, resume or roll back, and follow each facility's version and update status),
or from the command line (from a committed git tree; the manifest records the commit and who published):

```powershell
.\deploy\Build-Release.ps1 -Publish -Facilities "DATIM1,DATIM2" -Notes "what changed"   # pilot facilities
.\deploy\Manage-Update.ps1 status
.\deploy\Manage-Update.ps1 promote -Facilities all
.\deploy\Manage-Update.ps1 pause        # or resume
.\deploy\Manage-Update.ps1 rollback     # everyone back to the previous release
```

Storage access is the secrets file's connection string by default, or `-Account <storage account>` for a
team member's own Azure sign-in (role "Storage Blob Data Contributor"). A publish is refused when its
version isn't newer than the published one, an exe is never overwritten, and every manifest change is
conditional on nobody having changed it since it was read. The SHA-256 protects against corrupt or
mismatched downloads, not against someone who can write to the container; code signing can be added later.

## Report Output

`RadetWorkbook` generates one workbook with these sheets:

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
  | Prep | `prep.sql` | `query.yml` `prep-query` | 1980-01-01 |
  | AHD | `ahd.sql` | `query.yml` `ahd-query` | 1980-01-01 |

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

- Blob path: `<container>/<Reports:WorkbookFolder>/yyyy-MM-dd/<facility>_<datim>_yyyy_MM_dd.xlsx`, e.g.
  `autoreport/RADET/2026-11-23/Numan_General_Hospital_KXoSesiSLeE_2026_11_23.xlsx` (community centres:
  `community-center/RADET/...`).
- `Reports:WorkbookFolder` (`RADET`) is set in code (`Configuration/Settings.cs`), not in `appsettings.json`,
  because auto-update never replaces that file: a change of the default reaches every facility with the next
  release. Test packages set `TEST-RADET` in their own `appsettings.json` (see Test packages).
- Sheets are plain: a bold, unwrapped header row; no filter, fills or set column widths; dates as `yyyy-mm-dd`.

## Auth Behavior

**On in the release packages** (`Auth:Enabled = true` in `appsettings.json`), for security: before every report
cycle and force backfill the RSE asks BayCentral whether this PC - its BIOS serial number - is registered for the
facility's DATIM ID. A PC that is not registered runs no reports (the heartbeat shows the error) until it is.
Register PCs in BayCentral under **EMR Version Control admin -> Serial Numbers** (`/module-version-admin`), one row
per facility (DATIM ID + BIOS serial, from `(Get-CimInstance Win32_BIOS).SerialNumber` on the PC). When a
facility's PC is replaced, update its serial number there; the new PC is accepted within
`RetryWhenBlockedMinutes` (below). An authorised result is cached for the UTC day, a refusal for
`BlockedCacheMinutes` (5); with
`PermitWhenInconclusive = true` a PC that cannot reach BayCentral keeps reporting. Auto-update never changes
`appsettings.json`, so `Enabled` stays as installed; set it to `false` in one facility's file to switch the check
off there only.

**When a PC is refused:**
- The console (exe run directly) and the log show a `AHNi-RSE IS BLOCKED - THIS PC IS NOT AUTHORISED` banner with
  the facility, DATIM ID and PC serial to register. Run directly, the exe checks at startup, so the banner shows
  straight away; the service checks at its first cycle. A blocked cycle logs `Installation auth failed - cycle
  skipped` with the reason. When the check cannot be done at all - the DATIM ID cannot be read from the EMR
  database (e.g. PostgreSQL still starting after a reboot), or `Auth:ValidationEndpoint` is empty - the banner says
  `THE LICENCE CHECK COULD NOT BE DONE` with that reason, nothing is sent to BayCentral, and it is re-checked like
  any block (not recorded in `RseAuthFailures`, as it is not a registration problem).
- While blocked, the RSE checks again every `Auth:RetryWhenBlockedMinutes` (5; never less than
  `BlockedCacheMinutes`) instead of waiting for the next scheduled time, and runs the report as soon as BayCentral
  accepts the PC (cycle trigger `AuthRestored`), then returns to the normal schedule. Each re-check that still fails
  logs one `[Auth] Still blocked` line; the banner is shown again only if the reason changes. A block found by
  something else (e.g. a force backfill) while the schedule is waiting for its next time is noticed within a
  minute and starts the same re-checks.
- Force backfills wait: while blocked, pending jobs are left as they are; a job the check stops goes back to
  `pending` without using an attempt, with `Error` saying it is waiting for the PC to be accepted. Once the PC is
  accepted the backfill loop checks for jobs at once (not at its next poll) and runs them.
- The failure is recorded in the Azure table **`RseAuthFailures`** (`Auth:FailureTableName`; empty = off), apart
  from the heartbeat. One row per PC: PartitionKey = DATIM ID, RowKey = BIOS serial, with `Status` (`failing`, then
  `resolved` once the PC is accepted again), `Reason` (`refused`, `serial-unavailable`, or `inconclusive` when
  BayCentral cannot be reached and `PermitWhenInconclusive` is false), `FirstFailedAt`, `LastFailedAt`,
  `FailureCount` (failed checks, so it rises every 5 minutes while blocked), `ResolvedAt`, `FacilityName`, `State`, `MachineName`, `AppVersion` and `Package` (live / test).
  Rows with `Status = failing` are the PCs to fix in Serial Numbers. A failed write to the table is only logged; it
  never affects the check.

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

To produce one RADET workbook without installing the service or uploading anything, use
`tools/LocalRun`: it downloads the SQL from blob storage, runs it against a local LAMISPlus database
and saves the workbook to a folder (add an as-of date to run it as a force backfill, and `raw` after
the date for the raw report's Parquet files instead):

```powershell
dotnet run --project .\tools\LocalRun -- C:\path\appsettings.json C:\path\secrets.json numan C:\RseOut 2026-09-30
dotnet run --project .\tools\LocalRun -- C:\path\appsettings.json C:\path\secrets.json numan C:\RseOut 2026-09-30 raw
```

A custom script (from a file; its parameters after the dates as `name=value`), saved under
`C:\RseOut\custom-reports\`:

```powershell
dotnet run --project .\tools\LocalRun -- C:\path\appsettings.json C:\path\secrets.json numan C:\RseOut custom C:\path\script.sql 2024-01-01 2026-09-30 sex=Female min_age=15
```

`jobs <jobs table> <heartbeat table>` makes LocalRun act as the database's facility for force backfills in
**test** tables (it refuses the live ones): it writes that facility's heartbeat row, then claims and runs its
pending jobs once. Workbooks and raw reports are saved locally; custom results go to the
`custom-reports-test` container so a Backfill app on the same test tables can download and combine them.

```powershell
dotnet run --project .\tools\LocalRun -- C:\path\appsettings.json C:\path\secrets.json numan C:\RseOut jobs ForceBackfillJobsLocal RseLocalHeartbeat
```

`secrets.json` holds only `{ "ConnectionStrings": { "AzureBlobStorage": "..." } }`; keep it out of git.
The local Postgres password (user `postgres`) is read from the `LOCALRUN_DB_PASSWORD` environment variable
(`$env:LOCALRUN_DB_PASSWORD = '...'` first) or PostgreSQL's own `%APPDATA%\postgresql\pgpass.conf` - never put it in code.

## Community centres

The same exe serves gen-pop facilities and community centres. One setting in `appsettings.json`, in the
`Facility` section right under `Database`, decides which this facility is - nothing is looked up:

```jsonc
"Facility": {
  "IsCommunityCentre": false,                       // true at community centres
  "CareExcludedSheets": [ "PMTCTHTS", "Maternal" ]
}
```

| | Gen-pop (`false`, default) | Community centre (`true`) |
|---|---|---|
| Container | `Storage:Azure:ContainerName` (`autoreport`) | `Storage:Azure:CareContainerName` (`community-center`) |
| File name | `Reports:FileNameFormat` (`{facility}_{datim}_{date}`) | `Reports:CareFileNameFormat` (`{facility}_{datim}_{date}`) |
| Sheets | all 7 | without `CareExcludedSheets`: RADET, HTS, Index, Prep, AHD (as community-centre uploads have always been) |

The release script builds both packages (`AHNi-RSE-<version>.zip` and `AHNi-RSE-<version>-community-centre.zip`,
identical except for this setting); `Install.bat` shows which type it is about to install and waits before
installing. The service logs the type at startup. To change a facility's type, edit the setting and restart
the service.

`CareExcludedSheets` takes `Reports:Scripts` names; left-out sheets are not queried at all. `[ "none" ]`
keeps every sheet; RADET can't be left out. Force backfills follow the same rules (raw reports skip the same
reports and say so in the manifest), and the job records the container so download links point to it.

## Publish

Build the facility packages with one command (from the repo folder):

```powershell
.\deploy\Build-Release.ps1
```

It publishes **one self-contained `AHNiRSE.exe`** for win-x64 (about 41 MB: .NET and every library are inside it,
so facility servers need nothing installed), fills the live storage and database connection strings into the package's
`appsettings.json` from a secrets file kept **outside the repo** (default `..\release.secrets.json`; see
`deploy\release.secrets.example.json`), and writes `..\releases\AHNi-RSE-<version>.zip` (gen-pop) and `AHNi-RSE-<version>-community-centre.zip`
(the same with `Facility:IsCommunityCentre` = `true`). It refuses to build
if a connection string is still a placeholder, `LocalUploadFolder` is set, a test table name is in use, or
the build isn't a single self-contained exe, and it deletes its publish folder afterwards. The package is
four files: `AHNiRSE.exe`, `appsettings.json`, `Install.bat` and `Deploy-AHNi-RSEService.ps1`.

The package's `appsettings.json` replaces the facility's on install (the installer keeps the old one as
`appsettings.json.bak-<date>`), which is why the package must carry the real values. The repo's
`appsettings.json` keeps placeholders - never commit real ones.

Installed over an older multi-file version, the installer moves that version's loose runtime files (`*.dll`,
`*.deps.json`, `*.runtimeconfig.json`, `createdump.exe`) and any nested `publish\` copies of older builds into
`<install folder>\previous-version-<date>\`, with a copy of the old exe. Logs and settings stay. Nothing is
deleted: remove that folder once the new version is confirmed working, or move its files back to roll back.

Each build is stamped with its date and time as the version (e.g. `2026.11.5.1432`); the heartbeat and
backfill jobs report it as `AppVersion`, so BayCentral shows which build each facility runs. Pass
`-Version 2026.11.6.1` to the script (or `-p:Version=...` to `dotnet publish`) for a fixed number.

- `appsettings.Development*.json`, `tools\` and `deploy\*.json` are not included
- Release PDB symbols are disabled

### Test packages (run beside the installed service)

```powershell
.\deploy\Build-Release.ps1 -Test
```

writes `AHNi-RSE-<version>-test.zip` and `AHNi-RSE-<version>-community-centre-test.zip`, for trying this build at
a facility while its installed (older) service keeps running untouched. They hold `AHNiRSE.exe`, `appsettings.json`
and `Run-Test.bat` - no `Install.bat`, which would replace the service. Unzip into a folder of its own (e.g.
`C:\AHNi-RSE-Test`) and double-click `Run-Test.bat`: the RSE runs in that window until it is closed, with logs in
`<folder>\logs`. Everything works as in the service - scheduled cycles, force backfills (workbook, raw, custom SQL)
from the Backfill app - except that, not being the Windows service, it never installs an update.

- Workbooks go to `TEST-RADET/`: the script adds `Reports:WorkbookFolder` = `TEST-RADET` to the package's
  `appsettings.json` (installed releases use the code default, `RADET`).
- Its staging folder is `C:\Reports\StagingTest`, so it shares no files with the service.
- It uses the live `ForceBackfillJobs` and `FacilityHeartbeat` tables. The old service has no force backfill, so
  every job is the test build's; both write the facility's heartbeat row, so the version and last report BayCentral
  shows switch between the two while both run.
- Both run the scheduled cycles, so the facility's database does the RADET queries twice at those times.

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

Logs are in the `logs` folder next to `AHNiRSE.exe` (`C:\Services\AHNi-RSE\logs` for the service):
one `ahni-rse-<date>.log` per day (30 days kept) and `cycle-audit.jsonl`, one line per report cycle.

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



