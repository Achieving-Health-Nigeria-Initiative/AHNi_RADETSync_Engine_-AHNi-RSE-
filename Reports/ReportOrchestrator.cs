using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Reports;

/// <summary>
/// What a force backfill uploaded: RADET rows (a custom script's rows), the workbook (or raw report manifest,
/// or custom result) URL, Azure's upload time, every blob path it wrote (empty for the workbook, which is just
/// the URL) and the container they are in when it isn't the facility's usual one (custom-reports).
/// </summary>
public sealed record BackfillResult(
    int Rows, string Url, DateTimeOffset UploadedAt, IReadOnlyList<string> Files, string? Container = null);

/// <summary>
/// Runs the RADET workbook for a scheduled cycle (RunAsync) or a force backfill (RunBackfillAsync):
/// registration check, the workbook within its timeout, the cycle audit and, for scheduled cycles,
/// the heartbeat's Last RADET / error.
///
/// One cycle at a time: a scheduled cycle that finds another running is skipped, unless the one
/// running is a force backfill holding the lock (then it waits, so no scheduled upload is lost).
/// </summary>
public class ReportOrchestrator(
    InstallationAuthService auth,
    RadetWorkbook workbook,
    RawReport rawReport,
    CustomQuery customQuery,
    ReportScripts scripts,
    AzureHeartbeatService heartbeat,
    IDatabaseService database,
    IReportClock clock,
    ReportCycleAudit cycleAudit,
    IOptions<ReportSettings> reportOptions,
    IOptions<ForceBackfillSettings> backfillOptions,
    ILogger<ReportOrchestrator> logger)
{
    private const string AuthFailed = "Installation auth failed";
    private const string NothingUploaded = "No report was uploaded - see the script errors in the log";

    private readonly ReportSettings _settings = reportOptions.Value;
    private readonly ForceBackfillSettings _backfill = backfillOptions.Value;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    // Set while a force backfill holds _cycleLock: a scheduled cycle then waits instead of skipping.
    private volatile bool _backfillHoldsCycleLock;

    // Scheduled cycles and force backfills in progress (including those waiting for _cycleLock).
    private int _activeRuns;

    // 1 while auto-update holds _cycleLock to install a new version: nothing new starts.
    private int _updateHold;

    /// <summary>True while auto-update is installing a new version; no cycle or backfill starts.</summary>
    public bool UpdatePending => Volatile.Read(ref _updateHold) == 1;

    /// <summary>
    /// Takes the cycle lock for auto-update when no cycle or force backfill is running, so none can start
    /// while the service is being replaced. False when something is running; try again later.
    /// </summary>
    public bool TryHoldForUpdate()
    {
        if (!_cycleLock.Wait(0))
            return false;

        Interlocked.Exchange(ref _updateHold, 1);
        if (Volatile.Read(ref _activeRuns) == 0)
            return true;

        Interlocked.Exchange(ref _updateHold, 0);
        _cycleLock.Release();
        return false;
    }

    /// <summary>Gives the lock back when an update could not be started.</summary>
    public void ReleaseUpdateHold()
    {
        if (Interlocked.Exchange(ref _updateHold, 0) == 1)
            _cycleLock.Release();
    }

    /// <summary>A scheduled cycle as of today (WAT). Never throws; failures go to the heartbeat and audit.</summary>
    public async Task<(int TotalRows, string UploadUrl)> RunAsync(
        CancellationToken ct = default,
        string triggeredBy = "Cron")
    {
        if (!await _cycleLock.WaitAsync(0, ct))
        {
            if (UpdatePending)
            {
                logger.LogWarning("[Orchestrator] Skipped - a new RSE version is being installed.");
                return (0, string.Empty);
            }

            if (!_backfillHoldsCycleLock)
            {
                logger.LogWarning("[Orchestrator] Skipped - previous cycle still running.");
                return (0, string.Empty);
            }

            logger.LogInformation("[Orchestrator] Waiting for the running force backfill to finish.");
            await _cycleLock.WaitAsync(ct);
        }

        Interlocked.Increment(ref _activeRuns);
        var watToday = clock.TodayInWat();
        var startWat = clock.NowInWat();
        var slot = $"{(startWat.Hour / 2) * 2:00}:00";
        var context = new CycleContext(watToday, watToday, NewCycleId(), triggeredBy);
        var facility = GetFacilitySafe();

        using var scope = logger.BeginScope(new { context.CycleId });
        var sw = Stopwatch.StartNew();
        Task AuditAsync(bool authPassed, int rows, string url, string error) =>
            SafeRecordAuditAsync(context, startWat, slot, facility, authPassed, rows, url, error, sw.ElapsedMilliseconds);

        logger.LogInformation(
            "[Orchestrator] Cycle START | CycleId={CycleId} | RunDate={RunDate} (WAT) | Trigger={Trigger} | Timeout={Timeout}s",
            context.CycleId, watToday.ToString("yyyy-MM-dd"), triggeredBy, _settings.GlobalCycleTimeoutSeconds);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.GlobalCycleTimeoutSeconds)));

            if (!await auth.ValidateInstallationAsync(cts.Token))
            {
                logger.LogError("[Orchestrator] {Msg}", AuthFailed);
                heartbeat.SetReportError(AuthFailed);
                await AuditAsync(authPassed: false, 0, string.Empty, AuthFailed);
                return (0, string.Empty);
            }

            logger.LogInformation(
                "[Orchestrator] Facility: {Name} ({DatimId}) [{State}]",
                facility.FacilityName, facility.DatimId, facility.State);

            if (await RunWorkbookAsync(context, cts.Token, ct) is not { } result)
            {
                // Nothing reached blob storage, so Last RADET keeps the previous upload's time.
                logger.LogError("[Orchestrator] {Msg} (CycleId={Id})", NothingUploaded, context.CycleId);
                heartbeat.SetReportError(NothingUploaded);
                await AuditAsync(authPassed: true, 0, string.Empty, NothingUploaded);
                return (0, string.Empty);
            }

            // Written to the heartbeat table now, with Azure's upload time, rather than on the next ping.
            await heartbeat.RecordReportUploadAsync(
                facility.FacilityName, facility.DatimId, facility.State, result.UploadedAt, result.Rows, result.Url, ct);

            logger.LogInformation(
                "[Orchestrator] COMPLETE | CycleId={CycleId} | RunDate={RunDate} | {Rows} rows | {Url} | {Ms}ms",
                context.CycleId, watToday.ToString("yyyy-MM-dd"), result.Rows, result.Url, sw.ElapsedMilliseconds);
            await AuditAsync(authPassed: true, result.Rows, result.Url, string.Empty);
            return (result.Rows, result.Url);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var msg = $"Timeout after {_settings.GlobalCycleTimeoutSeconds}s";
            logger.LogWarning("[Orchestrator] {Msg} (CycleId={Id})", msg, context.CycleId);
            heartbeat.SetReportError(msg);
            await AuditAsync(authPassed: true, 0, string.Empty, msg);
            return (0, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Orchestrator] FAILED | CycleId={Id} | {Msg}", context.CycleId, ex.Message);
            heartbeat.SetReportError($"{ex.Message} (CycleId: {context.CycleId})");
            await AuditAsync(authPassed: true, 0, string.Empty, ex.Message);
            return (0, string.Empty);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRuns);
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// Runs one force backfill as of <paramref name="asOfDate"/>: the RADET workbook, uploaded to
    /// RADET/&lt;asOfDate&gt;/ (report type default), the raw report, uploaded to raw-report/&lt;asOfDate&gt;/
    /// (report type raw), or a custom script's result (report type custom, <paramref name="custom"/>; see
    /// CustomQuery). It never touches the heartbeat (Last RADET, Status). Throws when nothing was uploaded, so
    /// the caller can mark the job failed.
    ///
    /// It runs alongside a scheduled cycle unless ForceBackfill:RunAlongsideSchedule is false, or it is a
    /// workbook for today (both would write the same file); then it takes the cycle lock, and a scheduled
    /// cycle that comes due waits for it rather than being skipped.
    /// </summary>
    public async Task<BackfillResult> RunBackfillAsync(
        DateOnly asOfDate,
        string jobId,
        string reportType = BackfillReportType.Default,
        CustomJob? custom = null,
        CancellationToken ct = default)
    {
        if (reportType == BackfillReportType.Custom && custom is null)
            throw new ArgumentException("A custom job needs its script details.", nameof(custom));

        // Counted from here, so auto-update never installs while a backfill runs or waits.
        Interlocked.Increment(ref _activeRuns);
        try
        {
            return await RunBackfillCoreAsync(asOfDate, jobId, reportType, custom, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeRuns);
        }
    }

    private async Task<BackfillResult> RunBackfillCoreAsync(
        DateOnly asOfDate, string jobId, string reportType, CustomJob? custom, CancellationToken ct)
    {
        var raw = reportType == BackfillReportType.Raw;
        var isCustom = reportType == BackfillReportType.Custom;
        // Only a workbook for today writes the same file as a scheduled cycle. While an update holds the lock,
        // a backfill that would run alongside waits for it like any other.
        var holdCycleLock = !_backfill.RunAlongsideSchedule
                            || (!raw && !isCustom && asOfDate == clock.TodayInWat())
                            || UpdatePending;
        if (holdCycleLock)
        {
            if (!await _cycleLock.WaitAsync(0, ct))
            {
                logger.LogInformation("[Backfill] Waiting for the running report cycle to finish.");
                await _cycleLock.WaitAsync(ct);
            }
            _backfillHoldsCycleLock = true;
        }

        var startWat = clock.NowInWat();
        var context = new CycleContext(asOfDate, asOfDate, NewCycleId(), $"ForceBackfill:{jobId}", Backfill: true);
        var facility = GetFacilitySafe();

        using var scope = logger.BeginScope(new { context.CycleId });
        var sw = Stopwatch.StartNew();
        var rows = 0;
        var url = string.Empty;
        var error = string.Empty;

        logger.LogInformation(
            "[Backfill] START | Job={Job} | Type={Type} | CycleId={CycleId} | AsOf={AsOf} | AlongsideSchedule={Alongside}",
            jobId, reportType, context.CycleId, asOfDate.ToString("yyyy-MM-dd"), !holdCycleLock);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.GlobalCycleTimeoutSeconds)));

            if (!await auth.ValidateInstallationAsync(cts.Token))
                throw new InvalidOperationException(AuthFailed);

            BackfillResult result;
            if (isCustom)
            {
                result = await RunCustomAsync(context, custom!, cts.Token);
            }
            else if (raw)
            {
                result = await RunRawReportAsync(context, cts.Token);
            }
            else
            {
                var workbookResult = await RunWorkbookAsync(context, cts.Token, ct)
                    ?? throw new InvalidOperationException(NothingUploaded);
                result = new BackfillResult(workbookResult.Rows, workbookResult.Url, workbookResult.UploadedAt, []);
            }
            (rows, url) = (result.Rows, result.Url);

            logger.LogInformation(
                "[Backfill] COMPLETE | Job={Job} | Type={Type} | AsOf={AsOf} | {Rows} rows | {Url} | {Ms}ms",
                jobId, reportType, asOfDate.ToString("yyyy-MM-dd"), rows, url, sw.ElapsedMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = $"Timeout after {_settings.GlobalCycleTimeoutSeconds}s";
            throw new TimeoutException(error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            if (holdCycleLock)
            {
                _backfillHoldsCycleLock = false;
                _cycleLock.Release();
            }

            await SafeRecordAuditAsync(
                context, startWat, isCustom ? "backfill-custom" : raw ? "backfill-raw" : "backfill", facility, authPassed: error != AuthFailed,
                rows, url, error, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// The raw report within the RADET entry's TimeoutSeconds (or Reports:DefaultScriptTimeoutSeconds),
    /// the same limit as the workbook. Throws with the reason when it fails or times out.
    /// </summary>
    private async Task<BackfillResult> RunRawReportAsync(CycleContext context, CancellationToken cycleToken)
    {
        var timeout = Math.Max(30, scripts.Find(ReportScripts.Radet)?.TimeoutSeconds ?? _settings.DefaultScriptTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            var result = await rawReport.RunAsync(context, cts.Token);
            return new BackfillResult(result.RadetRows, result.ManifestUrl, result.UploadedAt, result.Files);
        }
        catch (OperationCanceledException) when (!cycleToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Raw report timed out after {timeout}s");
        }
        finally
        {
            MemoryPressureHelper.ReleaseAfterCycle(logger);
        }
    }

    /// <summary>
    /// A custom script within CustomQuery.JobTimeoutSeconds (CustomScript:TimeoutSeconds for the query itself,
    /// which PostgreSQL enforces). Throws with the reason when it fails or times out.
    /// </summary>
    private async Task<BackfillResult> RunCustomAsync(CycleContext context, CustomJob job, CancellationToken cycleToken)
    {
        var timeout = customQuery.JobTimeoutSeconds;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            return await customQuery.RunAsync(job, context, cts.Token);
        }
        catch (OperationCanceledException) when (!cycleToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Custom script timed out after {timeout}s");
        }
        finally
        {
            MemoryPressureHelper.ReleaseAfterCycle(logger);
        }
    }

    /// <summary>
    /// The workbook within the RADET entry's TimeoutSeconds (or Reports:DefaultScriptTimeoutSeconds).
    /// Returns null when it is switched off, fails or times out; the reason is logged. Rethrows only
    /// when the service is stopping (<paramref name="serviceToken"/>), so a backfill stays claimed.
    /// </summary>
    private async Task<(int Rows, string Url, DateTimeOffset UploadedAt)?> RunWorkbookAsync(
        CycleContext context,
        CancellationToken cycleToken,
        CancellationToken serviceToken)
    {
        var cfg = scripts.Find(ReportScripts.Radet);
        if (cfg is not { Enabled: true })
        {
            logger.LogWarning("[Orchestrator] RADET is missing or disabled in Reports:Scripts - nothing to run.");
            return null;
        }

        var timeout = Math.Max(30, cfg.TimeoutSeconds ?? _settings.DefaultScriptTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cycleToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var sw = Stopwatch.StartNew();

        logger.LogInformation(
            "[Orchestrator] -> RADET | RunDate={RunDate} | Timeout={Timeout}s", context.RunDate.ToString("yyyy-MM-dd"), timeout);

        try
        {
            var (rows, url, uploadedAt) = await workbook.RunAsync(context, cts.Token);
            logger.LogInformation("[Orchestrator] OK RADET | {Rows} rows | {Url} | {Ms}ms", rows, url, sw.ElapsedMilliseconds);
            return (rows, url, uploadedAt);
        }
        catch (OperationCanceledException) when (!cycleToken.IsCancellationRequested)
        {
            logger.LogError("[Orchestrator] TIMEOUT RADET after {Timeout}s ({Ms}ms elapsed)", timeout, sw.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex) when (!serviceToken.IsCancellationRequested)
        {
            logger.LogError(ex, "[Orchestrator] FAIL RADET ({Ms}ms): {Msg}", sw.ElapsedMilliseconds, ex.Message);
            return null;
        }
        finally
        {
            MemoryPressureHelper.ReleaseAfterCycle(logger);
        }
    }

    private static string NewCycleId() => Guid.NewGuid().ToString("N")[..8];

    private Facility GetFacilitySafe()
    {
        try
        {
            return database.GetFacilityInfo();
        }
        catch
        {
            return new Facility { FacilityName = string.Empty, DatimId = string.Empty, State = string.Empty };
        }
    }

    private async Task SafeRecordAuditAsync(
        CycleContext context, DateTimeOffset startWat, string slot, Facility facility,
        bool authPassed, int rows, string url, string error, long durationMs)
    {
        try
        {
            await cycleAudit.RecordAsync(new ReportCycleAuditEntry(
                CycleId: context.CycleId,
                Trigger: context.TriggeredBy,
                RunDateWat: context.RunDate,
                CycleStartWat: startWat,
                SlotLabel: slot,
                FacilityName: facility.FacilityName,
                DatimId: facility.DatimId,
                State: facility.State,
                AuthPassed: authPassed,
                Success: authPassed && error.Length == 0 && url.Length > 0,
                TotalRows: rows,
                UploadUrl: url,
                Error: error,
                DurationMs: durationMs), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Orchestrator] Failed to record cycle audit entry.");
        }
    }
}
