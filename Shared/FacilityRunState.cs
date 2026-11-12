namespace AHNiRSE.Shared;

/// <summary>
/// Thread-safe snapshot of the last report cycle result.
/// Shared between the report loop (writer) and heartbeat loop (reader).
/// Uses a lock to prevent torn reads across fields.
/// </summary>
public sealed class FacilityRunState
{
    private readonly object _lock = new();
    private string _facilityName = string.Empty;
    private string _datimId = string.Empty;
    private string _state = string.Empty;   
    private string _lastError = string.Empty;
    private string _lastUploadUrl = string.Empty;
    private int _lastRowCount;
    private DateTimeOffset? _lastReportRun;
    private bool _hasError;

    // ── Writers (called by report loop and startup) ───────────────────────────

    /// <summary>
    /// Called once at startup from the DB before the heartbeat loop fires.
    /// Prevents the first ping from writing a blank DESKTOP-XXXX row to
    /// Azure Table Storage before facility identity is known.
    /// Only sets values if datimId is non-empty — safe to call multiple times.
    /// </summary>
    public void SetFacilityIdentity(string facilityName, string datimId, string state = "")
    {
        if (string.IsNullOrWhiteSpace(datimId)) return;

        lock (_lock)
        {
            _facilityName = facilityName;
            _datimId = datimId;
            _state = state;
        }
    }

    /// <summary>
    /// Called by ReportOrchestrator after a successful report cycle.
    /// Updates all identity and report metadata in a single atomic write.
    /// </summary>
    public void ReportSucceeded(
        string facilityName,
        string datimId,
        string state,
        DateTimeOffset ranAt,
        int rowCount,
        string uploadUrl)
    {
        lock (_lock)
        {
            _facilityName = facilityName;
            _datimId = datimId;
            _state = state;
            _lastReportRun = ranAt;
            _lastRowCount = rowCount;
            _lastUploadUrl = uploadUrl;
            _lastError = string.Empty;
            _hasError = false;
        }
    }

    /// <summary>
    /// Called by ReportOrchestrator when the report cycle fails.
    /// Preserves the last known identity and report metadata.
    /// </summary>
    public void ReportFailed(string errorMessage)
    {
        lock (_lock)
        {
            _lastError = errorMessage;
            _hasError = true;
        }
    }

    // ── Readers (called by heartbeat loop) ────────────────────────────────────

    /// <summary>
    /// True once SetFacilityIdentity or ReportSucceeded has been called with
    /// a non-empty DatimId. The heartbeat loop skips pinging until this is true.
    /// </summary>
    public bool HasIdentity
    {
        get { lock (_lock) { return !string.IsNullOrWhiteSpace(_datimId); } }
    }

    /// <summary>
    /// Returns an immutable point-in-time snapshot safe to read outside the lock.
    /// </summary>
    public FacilityStateSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new FacilityStateSnapshot(
                FacilityName: _facilityName,
                DatimId: _datimId,
                State: _state,
                LastReportRun: _lastReportRun,
                LastRowCount: _lastRowCount,
                LastUploadUrl: _lastUploadUrl,
                LastError: _lastError,
                HasError: _hasError
            );
        }
    }
}

/// <summary>
/// Immutable point-in-time snapshot — safe to read outside the lock.
/// </summary>
public sealed record FacilityStateSnapshot(
    string FacilityName,
    string DatimId,
    string State,               
    DateTimeOffset? LastReportRun,
    int LastRowCount,
    string LastUploadUrl,
    string LastError,
    bool HasError
);
