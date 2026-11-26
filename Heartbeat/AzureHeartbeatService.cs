using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Heartbeat;

/// <summary>
/// Writes facility availability signals to Azure Table Storage.
///
/// RESILIENCE:
///   • MaxRetryAttempts configurable — set to 10 in appsettings.json.
///   • Per-attempt timeout (WriteTimeoutSeconds) — never hangs indefinitely.
///   • Exponential back-off with jitter — avoids thundering herd across facilities.
///   • After every attempt fails, pings pause for CooldownMinutes before trying again.
///   • RecordReportUploadAsync / SetReportError are fire-and-forget (no exceptions escape).
///   • PingAsync never throws, so a transient Azure Table outage cannot stop HeartbeatWorker.
///
/// TABLE LAYOUT:
///   PartitionKey = DatimId  (one row per facility, permanent)
///   RowKey       = "latest" (merged in place — O(1) read from BayCentral)
///
/// WRITERS (both merge, so neither blanks the other's fields):
///   • PingAsync, every IntervalMinutes — facility identity, MachineName, AppVersion, DateStyleVersion,
///     PackageType, Status, LastSeen, OfflineThresholdMinutes, LastReportError. DateStyleVersion is the
///     build's yyyy.M.d.Hmm version (older RSE builds, AppVersion 1.0.0.0, never write it). A test package
///     (Reports:WorkbookFolder TEST-RADET) writes TestAppVersion / TestDateStyleVersion / TestLastSeen instead,
///     so it never changes the installed service's version fields.
///   • RecordReportUploadAsync, right after a report upload — the same fields plus LastReportRun
///     (Azure's upload time), LastReportRows and LastReportUrl. The ping only resends these when
///     that write failed, so a restart (empty in-memory state) never wipes Last RADET.
///
/// BayCentral OFFLINE DETECTION:
///   BayCentral reads OfflineThresholdMinutes from the row.
///   If Now - LastSeen > OfflineThresholdMinutes → facility is OFFLINE.
///   Because HeartbeatWorker runs independently of report cycles,
///   the heartbeat keeps firing even during a failed or long-running report.
/// </summary>
public sealed class AzureHeartbeatService
{
    private readonly TableClient _table;
    private readonly FacilityRunState _state;
    private readonly UpdateStatus _update;
    private readonly HeartbeatSettings _cfg;
    private readonly ILogger<AzureHeartbeatService> _logger;
    private readonly string _machineName = Environment.MachineName;
    private readonly string _appVersion;
    private readonly string _dateStyleVersion;
    // A test package (workbooks to TEST-RADET) runs beside the installed service and writes the same row, so it
    // reports its version in TestAppVersion / TestDateStyleVersion and leaves AppVersion / DateStyleVersion to the service.
    private readonly bool _isTestPackage;
    private static readonly Random _jitter = new();

    // ── Cooldown ──────────────────────────────────────────────────────────────
    // After all retry attempts fail, PingAsync returns immediately and sets
    // _cooldownUntil to Now + CooldownMinutes. Every subsequent call checks
    // this before doing any network work. Once the cooldown expires, a full
    // retry cycle runs again.
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private const int CooldownMinutes = 4;   // rest period after total failure

    public AzureHeartbeatService(
        TableServiceClient tableService,
        FacilityRunState state,
        UpdateStatus updateStatus,
        FacilityProfile profile,
        IOptions<HeartbeatSettings> options,
        ILogger<AzureHeartbeatService> logger)
    {
        _state = state;
        _update = updateStatus;
        _cfg = options.Value;
        _logger = logger;
        _table = tableService.GetTableClient(_cfg.TableName);
        var version = typeof(AzureHeartbeatService).Assembly.GetName().Version;
        _appVersion = version?.ToString() ?? "unknown";
        _dateStyleVersion = DateStyleVersionOf(version);
        _isTestPackage = !profile.WorkbookFolder.Equals("RADET", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The version if it is date-style - yyyy.M.d.Hmm, set at build time (e.g. 2026.11.24.1657 = built
    /// 24 Nov 2026 at 16:57) - else empty (e.g. 1.0.0.0, or a version fixed with -p:Version).
    /// </summary>
    public static string DateStyleVersionOf(Version? v)
    {
        if (v is null || v.Major < 2000 || v.Minor is < 1 or > 12 || v.Build is < 1 or > 31 || v.Revision < 0)
            return string.Empty;
        if (v.Revision / 100 > 23 || v.Revision % 100 > 59) return string.Empty;
        try
        {
            _ = new DateTime(v.Major, v.Minor, v.Build); // rejects e.g. 31 February
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
        return v.ToString();
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        var snapshot = _state.Snapshot();

        if (string.IsNullOrWhiteSpace(snapshot.DatimId))
        {
            _logger.LogDebug("[Heartbeat] Skipping ping — facility identity not yet loaded.");
            return;
        }

        // ── COOLDOWN CHECK ────────────────────────────────────────────────────
        // If the previous full cycle failed entirely, we rest before retrying.
        if (DateTimeOffset.UtcNow < _cooldownUntil)
        {
            var remaining = (_cooldownUntil - DateTimeOffset.UtcNow).TotalSeconds;
            _logger.LogDebug(
                "[Heartbeat] In cooldown — skipping ping for {Sec:F0}s more.",
                remaining);
            return;
        }

        var entity = StatusEntity(snapshot);
        var resendReport = snapshot.ReportUnsent && snapshot.LastReportRun is not null;
        if (resendReport)
            AddReportFields(entity, snapshot);

        var maxAttempts = Math.Max(1, _cfg.MaxRetryAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeCts.CancelAfter(TimeSpan.FromSeconds(_cfg.WriteTimeoutSeconds));

            try
            {
                await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge, writeCts.Token);

                if (resendReport)
                    _state.ReportWritten(snapshot.LastReportRun!.Value);

                _logger.LogInformation(
                    "[Heartbeat] ✓ Ping sent — facility={Facility} status={Status}{Resent}",
                    snapshot.FacilityName, entity["Status"], resendReport ? " (with last report upload)" : "");

                return; // success — reset is implicit (cooldown only set on total failure)
            }
            catch (OperationCanceledException) when (
                writeCts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "[Heartbeat] Ping timed out after {Timeout}s (attempt {A}/{Max}).",
                    _cfg.WriteTimeoutSeconds, attempt, maxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("[Heartbeat] Ping cancelled — service shutting down.");
                return;
            }
            catch (Exception ex) when (IsConnectivityError(ex))
            {
                // ── CLEAN CONNECTIVITY MESSAGE ────────────────────────────────
                // SocketException 11001 / DNS failure = no internet.
                // Log ONE clean line — suppress the 40-line Azure SDK stack.
                _logger.LogWarning(
                    "[Heartbeat] No internet connection — cannot reach Azure " +
                    "(baycentral.table.core.windows.net). " +
                    "Attempt {A}/{Max}. BayCentral will detect OFFLINE after {Threshold} min.",
                    attempt, maxAttempts, _cfg.OfflineThresholdMinutes);

                // No point retrying immediately if DNS is dead — skip remaining attempts
                break;
            }
            catch (RequestFailedException ex) when (IsTransient(ex.Status))
            {
                _logger.LogWarning(
                    "[Heartbeat] Transient Azure error (HTTP {Status}) attempt {A}/{Max}: {Msg}",
                    ex.Status, attempt, maxAttempts, ex.Message);
            }
            catch (RequestFailedException ex)
            {
                // Non-retryable (403, 404, etc.)
                _logger.LogError(
                    "[Heartbeat] Non-retryable Azure error (HTTP {Status}): {Msg}",
                    ex.Status, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Heartbeat] Unexpected error attempt {A}/{Max}: {Type} — {Msg}",
                    attempt, maxAttempts, ex.GetType().Name, ex.Message);
            }

            if (attempt < maxAttempts)
            {
                var baseDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                var jitter = TimeSpan.FromMilliseconds(_jitter.Next(0, 1000));
                try { await Task.Delay(baseDelay + jitter, ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        // ── ALL ATTEMPTS FAILED — enter cooldown ──────────────────────────────
        _cooldownUntil = DateTimeOffset.UtcNow.AddMinutes(CooldownMinutes);

        _logger.LogWarning(
            "[Heartbeat] All {Max} ping attempt(s) failed — facility={Facility}. " +
            "Pausing heartbeat for {Cooldown} min. " +
            "BayCentral will detect OFFLINE after {Threshold} min if this continues.",
            maxAttempts, snapshot.FacilityName, CooldownMinutes, _cfg.OfflineThresholdMinutes);
    }

    // ── Connectivity error detector ───────────────────────────────────────────
    /// <summary>
    /// Returns true when the exception chain indicates a DNS / socket-level
    /// connectivity failure — i.e. "No such host is known" (SocketError 11001).
    /// These are not Azure SDK bugs; they mean the machine has no internet.
    /// </summary>
    private static bool IsConnectivityError(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is System.Net.Sockets.SocketException se &&
                se.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                                   or System.Net.Sockets.SocketError.NoData
                                   or System.Net.Sockets.SocketError.TryAgain)
                return true;

            if (current is System.Net.Http.HttpRequestException hre &&
                hre.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
                return true;

            current = current.InnerException;
        }
        return false;
    }

    private static bool IsTransient(int status) =>
        status is 429 or 500 or 502 or 503 or 504;

    // ── Row fields ────────────────────────────────────────────────────────────
    private TableEntity StatusEntity(FacilityStateSnapshot snapshot)
    {
        var update = _update.Snapshot();
        var entity = new TableEntity(snapshot.DatimId, "latest")
        {
            ["FacilityName"] = snapshot.FacilityName,
            ["DatimId"] = snapshot.DatimId,
            ["State"] = snapshot.State,
            ["MachineName"] = _machineName,
            ["Status"] = snapshot.HasError ? "error" : "online",
            ["LastSeen"] = DateTimeOffset.UtcNow,
            ["OfflineThresholdMinutes"] = _cfg.OfflineThresholdMinutes,
            ["LastReportError"] = snapshot.LastError,

            // Auto-update (see UpdateStatus); times as ISO-8601 strings like LastReportRun.
            ["UpdateStatus"] = update.Status,
            ["UpdateTargetVersion"] = update.TargetVersion,
            ["UpdateError"] = update.Error,
            ["UpdateLastChecked"] = update.LastChecked?.ToString("o") ?? string.Empty,
            ["UpdateLastInstalled"] = update.LastInstalled?.ToString("o") ?? string.Empty,
        };

        // Which RSE is running. DateStyleVersion is the build's yyyy.M.d.Hmm version (this RSE); older builds
        // (AppVersion 1.0.0.0) never write it, so a row without it is still on an old RSE. A test package
        // writes the Test* fields instead, so it cannot make the installed service look updated.
        if (_isTestPackage)
        {
            entity["TestAppVersion"] = _appVersion;
            entity["TestDateStyleVersion"] = _dateStyleVersion;
            entity["TestLastSeen"] = DateTimeOffset.UtcNow;
        }
        else
        {
            entity["AppVersion"] = _appVersion;
            entity["DateStyleVersion"] = _dateStyleVersion;
            entity["PackageType"] = "live";
        }
        return entity;
    }

    // LastReportRun stays an ISO-8601 string: BayCentral reads it with GetString and parses it.
    private static void AddReportFields(TableEntity entity, FacilityStateSnapshot snapshot)
    {
        entity["LastReportRun"] = snapshot.LastReportRun?.ToString("o") ?? string.Empty;
        entity["LastReportRows"] = snapshot.LastRowCount;
        entity["LastReportUrl"] = snapshot.LastUploadUrl;
    }

    // ── RecordReportUploadAsync / SetReportError / EnsureTableExistsAsync ─────
    public async Task RecordReportUploadAsync(
        string facilityName, string datimId, string state,
        DateTimeOffset uploadedAt, int rowCount, string uploadUrl,
        CancellationToken ct = default)
    {
        try { _state.ReportUploaded(facilityName, datimId, state, uploadedAt, rowCount, uploadUrl); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Heartbeat] Report upload state update failed.");
            return;
        }

        var snapshot = _state.Snapshot();
        if (string.IsNullOrWhiteSpace(snapshot.DatimId))
        {
            _logger.LogWarning("[Heartbeat] Report upload not written — facility identity not loaded.");
            return;
        }

        var entity = StatusEntity(snapshot);
        AddReportFields(entity, snapshot);

        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeCts.CancelAfter(TimeSpan.FromSeconds(_cfg.WriteTimeoutSeconds));

            try
            {
                await _table.UpsertEntityAsync(entity, TableUpdateMode.Merge, writeCts.Token);
                _state.ReportWritten(uploadedAt);

                _logger.LogInformation(
                    "[Heartbeat] ✓ Last report upload written — facility={Facility} uploadedAt={UploadedAt:o} rows={Rows}",
                    snapshot.FacilityName, uploadedAt, rowCount);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Heartbeat] Writing last report upload failed (attempt {A}/{Max}): {Type} — {Msg}",
                    attempt, maxAttempts, ex.GetType().Name, ex.Message);
            }

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogWarning("[Heartbeat] Last report upload will be sent with the next heartbeat ping.");
    }

    public void SetReportError(string errorMessage)
    {
        try { _state.ReportFailed(errorMessage); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "[Heartbeat] SetReportError state update failed."); }
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _table.CreateIfNotExistsAsync(ct);
                _logger.LogInformation("[Heartbeat] Table '{Table}' ready.", _cfg.TableName);
                return;
            }
            catch (Exception ex) when (IsConnectivityError(ex))
            {
                _logger.LogWarning(
                    "[Heartbeat] No internet — cannot verify Azure Table exists " +
                    "(attempt {A}/{Max}). Service will continue; heartbeat will retry later.",
                    attempt, maxAttempts);
                // Don't block startup — heartbeat cooldown will handle retries
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Heartbeat] Could not ensure table exists (attempt {A}/{Max}): {Msg}",
                    attempt, maxAttempts, ex.Message);

                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct);
            }
        }
    }
}
