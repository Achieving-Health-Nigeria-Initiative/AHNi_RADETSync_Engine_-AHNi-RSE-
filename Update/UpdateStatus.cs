namespace AHNiRSE.Update;

/// <summary>
/// Where auto-update stands, for the heartbeat row (UpdateStatus, UpdateTargetVersion, UpdateLastChecked,
/// UpdateLastInstalled, UpdateError), so BayCentral's facility health page shows who updated, who is waiting
/// and who rolled back.
///
/// Status values: Starting, Disabled, UpToDate, Paused, NotInRollout, Waiting (spreading downloads),
/// Downloading, WaitingForIdle, Installing, Installed, RolledBack, Failed, CheckFailed.
/// </summary>
public sealed class UpdateStatus
{
    private readonly object _lock = new();
    private string _status = "Starting";
    private string _target = string.Empty;
    private string _error = string.Empty;
    private DateTimeOffset? _lastChecked;
    private DateTimeOffset? _lastInstalled;

    public UpdateStatus()
    {
        // How the last update went, written by the updater before this process started.
        if (UpdateFiles.ReadResult(UpdateFiles.InstallDir) is not { } last)
            return;

        if (last.Status == "Installed")
            _lastInstalled = last.AtUtc;
        if (last.AtUtc > DateTimeOffset.UtcNow.AddDays(-1))
        {
            _status = last.Status;
            _target = last.Version;
            _error = last.Error;
        }
    }

    public void Set(string status, string target = "", string error = "")
    {
        lock (_lock)
        {
            _status = status;
            _target = target;
            _error = error;
        }
    }

    public void Checked()
    {
        lock (_lock) _lastChecked = DateTimeOffset.UtcNow;
    }

    public UpdateStatusSnapshot Snapshot()
    {
        lock (_lock)
            return new UpdateStatusSnapshot(_status, _target, _error, _lastChecked, _lastInstalled);
    }
}

public sealed record UpdateStatusSnapshot(
    string Status, string TargetVersion, string Error, DateTimeOffset? LastChecked, DateTimeOffset? LastInstalled);
