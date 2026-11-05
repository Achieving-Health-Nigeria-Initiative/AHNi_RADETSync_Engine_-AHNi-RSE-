namespace AHNiRSE.Configuration;

// ── Storage ───────────────────────────────────────────────────────────────────

public class StorageSettings
{
    public AzureStorageOptions Azure { get; set; } = new();
}

public class AzureStorageOptions
{
    public string ContainerName { get; set; } = string.Empty;
    public string ScriptContainerName { get; set; } = string.Empty;
    public Dictionary<string, string> BlobPrefixes { get; set; } = new();
    public int DownloadMaxRetryAttempts { get; set; } = 3;
    public int DownloadRetryDelaySeconds { get; set; } = 5;
    public int DownloadTimeoutSeconds { get; set; } = 300;
    public string TempDownloadPath { get; set; } = string.Empty;

    /// <summary>
    /// Testing only. When set, finished workbooks are copied to
    /// &lt;LocalUploadFolder&gt;\&lt;ContainerName&gt;\RADET\&lt;date&gt;\... instead of being uploaded.
    /// Leave empty on every facility.
    /// </summary>
    public string LocalUploadFolder { get; set; } = string.Empty;
}

// ── Heartbeat ─────────────────────────────────────────────────────────────────

public sealed class HeartbeatSettings
{
    /// <summary>Azure Table Storage table name.</summary>
    public string TableName { get; set; } = "FacilityHeartbeat";

    /// <summary>How often to ping. Default: 5 minutes.</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Used by BayCentral to decide online/offline.
    /// Stored in the heartbeat row so BayCentral reads it dynamically.
    /// </summary>
    public int OfflineThresholdMinutes { get; set; } = 6;

    /// <summary>
    /// How long to wait for a Table Storage write before giving up.
    /// Should be much shorter than IntervalMinutes.
    /// </summary>
    public int WriteTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Max retries on transient Table Storage failures before giving up for this cycle.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 2;
}

// ── Database ──────────────────────────────────────────────────────────────────

public class DatabaseSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 2;
}

// ── Auth ──────────────────────────────────────────────────────────────────────

public class AuthSettings
{
    public bool? Enabled { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public string ValidationEndpoint { get; set; } = string.Empty;
    public string LicenseTier { get; set; } = "Enterprise";
    public int RequestTimeoutSeconds { get; set; } = 15;
    public bool EnableDailyCache { get; set; } = true;
    public int BlockedCacheMinutes { get; set; } = 5;
    public string CachePath { get; set; } = string.Empty;
    // When true, an inconclusive result (WAF/proxy block, timeout) is treated as authorised
    // so that a Cloudflare or network issue does not stop report cycles.
    // Set this only as a temporary measure while the upstream block is being resolved.
    public bool PermitWhenInconclusive { get; set; } = false;
}

// ── Force backfill ────────────────────────────────────────────────────────────

/// <summary>
/// RADET backfills requested from BayCentral (Administration → Force Backfill). Each job asks for
/// the workbook as of one past date; it is uploaded to RADET/&lt;date&gt;/ like a scheduled run.
/// </summary>
public class ForceBackfillSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Azure table BayCentral writes the jobs to (same storage account as the heartbeat).</summary>
    public string TableName { get; set; } = "ForceBackfillJobs";

    /// <summary>How often to look for this facility's pending jobs.</summary>
    public int PollMinutes { get; set; } = 5;

    /// <summary>
    /// true = a backfill may run while a scheduled cycle runs (a backfill for today's date still waits).
    /// false = backfills never overlap a scheduled cycle; a cycle that comes due waits for the backfill.
    /// </summary>
    public bool RunAlongsideSchedule { get; set; } = true;

    /// <summary>Runs per job before it is marked failed (a restart mid-run counts as one).</summary>
    public int MaxAttempts { get; set; } = 3;
}

// ── Reports ───────────────────────────────────────────────────────────────────

public class ReportSettings
{
    public string CronSchedule { get; set; } = "0 */2 * * *";
    public List<string> CronSchedules { get; set; } = new();
    public bool RunOnStartup { get; set; } = true;
    public bool AllowOffScheduleStartupRun { get; set; } = false;
    public bool EnableCycleAudit { get; set; } = true;
    public string CycleAuditPath { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>
    /// Workbook file name (without .xlsx). {facility} = facility name with spaces as underscores,
    /// {datim} = DATIM ID, {date} = yyyy_MM_dd, e.g. Numan_General_Hospital_KXoSesiSLeE_2026_11_05.
    /// </summary>
    public string FileNameFormat { get; set; } = "{facility}_{datim}_{date}";
    public int GlobalCycleTimeoutSeconds { get; set; } = 3600;
    public int DefaultScriptTimeoutSeconds { get; set; } = 900;

    public List<ScriptConfig> Scripts { get; set; } = new();
}

public class ScriptConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Script path inside the blob script container, e.g. "report-script/radet.sql".</summary>
    public string BlobPath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>:facility_id. 0 = read it from the database (base_application_user id 1).</summary>
    public long FacilityId { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>:start_date as yyyy-MM-dd; empty = the script's default.</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>:end_date as yyyy-MM-dd; empty = today (WAT).</summary>
    public string EndDate { get; set; } = string.Empty;
}

