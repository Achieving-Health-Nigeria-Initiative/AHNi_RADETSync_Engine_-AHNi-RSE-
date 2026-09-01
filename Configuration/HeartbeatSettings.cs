namespace AHNiRSE.Configuration;

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
