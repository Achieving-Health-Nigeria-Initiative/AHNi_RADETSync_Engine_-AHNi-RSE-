using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Shared;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Infrastructure.Heartbeat;

/// <summary>
/// Writes facility availability signals to Azure Table Storage.
///
/// RESILIENCE IMPROVEMENTS (enterprise version):
///   • MaxRetryAttempts configurable — set to 10 in appsettings.json.
///   • Per-attempt timeout (WriteTimeoutSeconds) — never hangs indefinitely.
///   • Exponential back-off with jitter — avoids thundering herd across facilities.
///   • SetReportSuccess / SetReportError are fire-and-forget (no exceptions escape).
///   • PingAsync never throws — Worker loop catches at the boundary but this adds
///     a second safety layer so a transient Azure Table outage cannot crash the loop.
///
/// TABLE LAYOUT:
///   PartitionKey = DatimId  (one row per facility, permanent)
///   RowKey       = "latest" (always overwrite — O(1) read from BayCentral)
///
/// BayCentral OFFLINE DETECTION:
///   BayCentral reads OfflineThresholdMinutes from the row.
///   If Now - LastSeen > OfflineThresholdMinutes → facility is OFFLINE.
///   Because Worker.HeartbeatLoopAsync runs independently of report cycles,
///   the heartbeat keeps firing even during a failed or long-running report.
/// </summary>
public sealed class AzureHeartbeatService : IHeartbeatService
{
    // ── existing fields unchanged ─────────────────────────────────────────────
    private readonly TableClient _table;
    private readonly FacilityRunState _state;
    private readonly HeartbeatSettings _cfg;
    private readonly ILogger<AzureHeartbeatService> _logger;
    private readonly string _machineName = Environment.MachineName;
    private readonly string _appVersion;
    private static readonly Random _jitter = new();

    // ── NEW: cooldown state ───────────────────────────────────────────────────
    // After all retry attempts fail, PingAsync returns immediately and sets
    // _cooldownUntil to Now + CooldownMinutes. Every subsequent call checks
    // this before doing any network work. Once the cooldown expires, a full
    // retry cycle runs again.
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private const int CooldownMinutes = 4;   // rest period after total failure

    public AzureHeartbeatService(
        TableServiceClient tableService,
        FacilityRunState state,
        IOptions<HeartbeatSettings> options,
        ILogger<AzureHeartbeatService> logger)
    {
        _state = state;
        _cfg = options.Value;
        _logger = logger;
        _table = tableService.GetTableClient(_cfg.TableName);
        _appVersion = typeof(AzureHeartbeatService).Assembly
            .GetName().Version?.ToString() ?? "unknown";
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

        var entity = new TableEntity(snapshot.DatimId, "latest")
        {
            ["FacilityName"] = snapshot.FacilityName,
            ["DatimId"] = snapshot.DatimId,
            ["State"] = snapshot.State,
            ["MachineName"] = _machineName,
            ["AppVersion"] = _appVersion,
            ["Status"] = snapshot.HasError ? "error" : "online",
            ["LastSeen"] = DateTimeOffset.UtcNow,
            ["OfflineThresholdMinutes"] = _cfg.OfflineThresholdMinutes,
            ["LastReportRun"] = snapshot.LastReportRun?.ToString("o") ?? string.Empty,
            ["LastReportRows"] = snapshot.LastRowCount,
            ["LastReportUrl"] = snapshot.LastUploadUrl,
            ["LastReportError"] = snapshot.LastError,
        };

        var maxAttempts = Math.Max(1, _cfg.MaxRetryAttempts);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeCts.CancelAfter(TimeSpan.FromSeconds(_cfg.WriteTimeoutSeconds));

            try
            {
                await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, writeCts.Token);

                _logger.LogInformation(
                    "[Heartbeat] ✓ Ping sent — facility={Facility} status={Status}",
                    snapshot.FacilityName, entity["Status"]);

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

    // ── SetReportSuccess / SetReportError / EnsureTableExistsAsync unchanged ──
    public void SetReportSuccess(
        string facilityName, string datimId, string state,
        DateTimeOffset ranAt, int rowCount, string uploadUrl)
    {
        try { _state.ReportSucceeded(facilityName, datimId, state, ranAt, rowCount, uploadUrl); }
        catch (Exception ex)
        { _logger.LogWarning(ex, "[Heartbeat] SetReportSuccess state update failed."); }
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
