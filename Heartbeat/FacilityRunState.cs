namespace AHNiRSE.Heartbeat;

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
    private bool _reportUnsent;
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
    /// Called when a report upload succeeds. Updates all identity and report metadata in a
    /// single atomic write and marks the report fields as not yet written to the table.
    /// </summary>
    public void ReportUploaded(
        string facilityName,
        string datimId,
        string state,
        DateTimeOffset uploadedAt,
        int rowCount,
        string uploadUrl)
    {
        lock (_lock)
        {
            _facilityName = facilityName;
            _datimId = datimId;
            _state = state;
            _lastReportRun = uploadedAt;
            _lastRowCount = rowCount;
            _lastUploadUrl = uploadUrl;
            _lastError = string.Empty;
            _hasError = false;
            _reportUnsent = true;
        }
    }

    /// <summary>
    /// Called once the report fields for the upload at <paramref name="uploadedAt"/> are in the table.
    /// A newer upload recorded in the meantime stays unsent.
    /// </summary>
    public void ReportWritten(DateTimeOffset uploadedAt)
    {
        lock (_lock)
        {
            if (_lastReportRun == uploadedAt)
                _reportUnsent = false;
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
    /// True once SetFacilityIdentity or ReportUploaded has been called with
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
                HasError: _hasError,
                ReportUnsent: _reportUnsent
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
    bool HasError,
    bool ReportUnsent
);
