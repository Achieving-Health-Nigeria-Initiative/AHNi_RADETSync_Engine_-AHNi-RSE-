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
    public bool CleanStagingAfterDeploy { get; set; } = true;
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
    public string WorkerApiKeyHeaderName { get; set; } = "X-AHNi-RSE-Key";
    public string WorkerApiKey { get; set; } = string.Empty;
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
    public int GlobalCycleTimeoutSeconds { get; set; } = 3600;
    public int DefaultScriptTimeoutSeconds { get; set; } = 900;
    public int MaxParallelScripts { get; set; } = 1;

    /// <summary>
    /// Folder holding the scripts named by <see cref="ScriptConfig.LocalPath"/>. A relative path is
    /// resolved against the app's content root at startup.
    /// </summary>
    public string LocalScriptFolder { get; set; } = string.Empty;

    public List<ScriptConfig> Scripts { get; set; } = new();
}

public class ScriptConfig
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Script path inside the blob script container, e.g. "radet_query_new.sql".</summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// When set, the script is read from this file in Reports:LocalScriptFolder instead of
    /// being downloaded from <see cref="BlobPath"/>.
    /// </summary>
    public string LocalPath { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>:facility_id. 0 = read it from the database (base_application_user id 1).</summary>
    public long FacilityId { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>:start_date as yyyy-MM-dd; empty = the script's default.</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>:end_date as yyyy-MM-dd; empty = today (WAT).</summary>
    public string EndDate { get; set; } = string.Empty;
}

