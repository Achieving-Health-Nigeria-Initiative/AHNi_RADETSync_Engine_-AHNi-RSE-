using System.Diagnostics;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Clock;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Application.Services;

/// <summary>
/// Coordinates one full report cycle: auth, facility load, scripts, heartbeat.
/// </summary>
public class ReportOrchestrator(
    IAuthService auth,
    ScriptManager scriptManager,
    IHeartbeatService heartbeat,
    IDatabaseService database,
    IReportClock clock,
    IReportCycleAudit cycleAudit,
    IOptions<ReportSettings> reportOptions,
    IOptions<ForceBackfillSettings> backfillOptions,
    ILogger<ReportOrchestrator> logger)
{
    private readonly ReportSettings _settings = reportOptions.Value;
    private readonly ForceBackfillSettings _backfill = backfillOptions.Value;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    // Set while a force backfill holds _cycleLock: a scheduled cycle then waits instead of skipping.
    private volatile bool _backfillHoldsCycleLock;

    public Task<(string FacilityName, string DatimId, string State)> LoadFacilityIdentityAsync(
        CancellationToken ct = default)
    {
        try
        {
            var facility = database.GetFacilityInfo();
            if (string.IsNullOrWhiteSpace(facility.DatimId))
            {
                logger.LogWarning("[Orchestrator] DatimId empty - check DB config.");
                return Task.FromResult((string.Empty, string.Empty, string.Empty));
            }

            return Task.FromResult((facility.FacilityName, facility.DatimId, facility.State));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Orchestrator] LoadFacilityIdentity failed.");
            return Task.FromResult((string.Empty, string.Empty, string.Empty));
        }
    }

    public async Task<(int TotalRows, string UploadUrl)> RunAsync(
        CancellationToken ct = default,
        string triggeredBy = "Cron")
    {
        if (!await _cycleLock.WaitAsync(0, ct))
        {
            if (!_backfillHoldsCycleLock)
            {
                logger.LogWarning("[Orchestrator] Skipped - previous cycle still running.");
                return (0, string.Empty);
            }

            logger.LogInformation("[Orchestrator] Waiting for the running force backfill to finish.");
            await _cycleLock.WaitAsync(ct);
        }

        var watToday = clock.TodayInWat();
        var cycleStartWat = clock.NowInWat();
        var slotLabel = $"{(cycleStartWat.Hour / 2) * 2:00}:00";
        var cycleId = Guid.NewGuid().ToString("N")[..8];
        var facility = GetFacilitySafe();

        var context = new CycleContext(
            RunDate: watToday,
            WatToday: watToday,
            CycleId: cycleId,
            TriggeredBy: triggeredBy);

        using var scope = logger.BeginScope(new { CycleId = cycleId });
        var sw = Stopwatch.StartNew();

        logger.LogInformation(
            "[Orchestrator] Cycle START | CycleId={CycleId} | RunDate={RunDate} (WAT) | Trigger={Trigger} | Timeout={Timeout}s",
            cycleId,
            watToday.ToString("yyyy-MM-dd"),
            triggeredBy,
            _settings.GlobalCycleTimeoutSeconds);

        try
        {
            var timeout = Math.Max(30, _settings.GlobalCycleTimeoutSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeout));

            if (!await auth.ValidateInstallationAsync(cts.Token))
            {
                const string msg = "Installation auth failed";
                logger.LogError("[Orchestrator] {Msg}", msg);
                heartbeat.SetReportError(msg);
                sw.Stop();
                await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                    CycleId: cycleId,
                    Trigger: triggeredBy,
                    RunDateWat: watToday,
                    CycleStartWat: cycleStartWat,
                    SlotLabel: slotLabel,
                    FacilityName: facility.FacilityName,
                    DatimId: facility.DatimId,
                    State: facility.State,
                    AuthPassed: false,
                    Success: false,
                    TotalRows: 0,
                    UploadUrl: string.Empty,
                    Error: msg,
                    DurationMs: sw.ElapsedMilliseconds), ct);
                return (0, string.Empty);
            }

            logger.LogInformation(
                "[Orchestrator] Facility: {Name} ({DatimId}) [{State}]",
                facility.FacilityName, facility.DatimId, facility.State);

            var (rows, url, uploadedAt) = await scriptManager.RunEnabledAsync(context, cts.Token);
            sw.Stop();

            if (uploadedAt is null)
            {
                // Nothing reached blob storage, so Last RADET keeps the previous upload's time.
                const string msg = "No report was uploaded - see the script errors in the log";
                logger.LogError("[Orchestrator] {Msg} (CycleId={Id})", msg, cycleId);
                heartbeat.SetReportError(msg);
                await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                    CycleId: cycleId,
                    Trigger: triggeredBy,
                    RunDateWat: watToday,
                    CycleStartWat: cycleStartWat,
                    SlotLabel: slotLabel,
                    FacilityName: facility.FacilityName,
                    DatimId: facility.DatimId,
                    State: facility.State,
                    AuthPassed: true,
                    Success: false,
                    TotalRows: rows,
                    UploadUrl: string.Empty,
                    Error: msg,
                    DurationMs: sw.ElapsedMilliseconds), ct);
                return (rows, string.Empty);
            }

            // Written to the heartbeat table now, with Azure's upload time, rather than on the next ping.
            await heartbeat.RecordReportUploadAsync(
                facility.FacilityName,
                facility.DatimId,
                facility.State,
                uploadedAt.Value,
                rows,
                url,
                ct);

            logger.LogInformation(
                "[Orchestrator] COMPLETE | CycleId={CycleId} | RunDate={RunDate} | {Rows} rows | {Url} | {Ms}ms",
                cycleId, watToday.ToString("yyyy-MM-dd"), rows, url, sw.ElapsedMilliseconds);

            await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                CycleId: cycleId,
                Trigger: triggeredBy,
                RunDateWat: watToday,
                CycleStartWat: cycleStartWat,
                SlotLabel: slotLabel,
                FacilityName: facility.FacilityName,
                DatimId: facility.DatimId,
                State: facility.State,
                AuthPassed: true,
                Success: true,
                TotalRows: rows,
                UploadUrl: url,
                Error: string.Empty,
                DurationMs: sw.ElapsedMilliseconds), ct);

            return (rows, url);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            var msg = $"Timeout after {_settings.GlobalCycleTimeoutSeconds}s";
            logger.LogWarning("[Orchestrator] {Msg} (CycleId={Id})", msg, cycleId);
            heartbeat.SetReportError(msg);
            await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                CycleId: cycleId,
                Trigger: triggeredBy,
                RunDateWat: watToday,
                CycleStartWat: cycleStartWat,
                SlotLabel: slotLabel,
                FacilityName: facility.FacilityName,
                DatimId: facility.DatimId,
                State: facility.State,
                AuthPassed: true,
                Success: false,
                TotalRows: 0,
                UploadUrl: string.Empty,
                Error: msg,
                DurationMs: sw.ElapsedMilliseconds), ct);
            return (0, string.Empty);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[Orchestrator] FAILED | CycleId={Id} | {Msg}", cycleId, ex.Message);
            heartbeat.SetReportError($"{ex.Message} (CycleId: {cycleId})");
            await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                CycleId: cycleId,
                Trigger: triggeredBy,
                RunDateWat: watToday,
                CycleStartWat: cycleStartWat,
                SlotLabel: slotLabel,
                FacilityName: facility.FacilityName,
                DatimId: facility.DatimId,
                State: facility.State,
                AuthPassed: true,
                Success: false,
                TotalRows: 0,
                UploadUrl: string.Empty,
                Error: ex.Message,
                DurationMs: sw.ElapsedMilliseconds), ct);
            return (0, string.Empty);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    /// <summary>
    /// Runs one force backfill: the RADET workbook as of <paramref name="asOfDate"/>, uploaded to
    /// RADET/&lt;asOfDate&gt;/. It never touches the heartbeat (Last RADET, Status). Throws when nothing
    /// was uploaded, so the caller can mark the job failed.
    ///
    /// It runs alongside a scheduled cycle unless ForceBackfill:RunAlongsideSchedule is false or the
    /// date is today (both would write the same file); then it takes the cycle lock, and a scheduled
    /// cycle that comes due waits for it rather than being skipped.
    /// </summary>
    public async Task<(int Rows, string UploadUrl, DateTimeOffset UploadedAt)> RunBackfillAsync(
        DateOnly asOfDate,
        string jobId,
        CancellationToken ct = default)
    {
        var holdCycleLock = !_backfill.RunAlongsideSchedule || asOfDate == clock.TodayInWat();
        if (holdCycleLock)
        {
            if (!await _cycleLock.WaitAsync(0, ct))
            {
                logger.LogInformation("[Backfill] Waiting for the running report cycle to finish.");
                await _cycleLock.WaitAsync(ct);
            }
            _backfillHoldsCycleLock = true;
        }

        var cycleStartWat = clock.NowInWat();
        var cycleId = Guid.NewGuid().ToString("N")[..8];
        var facility = GetFacilitySafe();
        var context = new CycleContext(
            RunDate: asOfDate,
            WatToday: asOfDate,
            CycleId: cycleId,
            TriggeredBy: $"ForceBackfill:{jobId}",
            Backfill: true);

        using var scope = logger.BeginScope(new { CycleId = cycleId });
        var sw = Stopwatch.StartNew();
        var rows = 0;
        var url = string.Empty;
        var error = string.Empty;

        logger.LogInformation(
            "[Backfill] START | Job={Job} | CycleId={CycleId} | AsOf={AsOf} | AlongsideSchedule={Alongside}",
            jobId, cycleId, asOfDate.ToString("yyyy-MM-dd"), !holdCycleLock);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _settings.GlobalCycleTimeoutSeconds)));

            if (!await auth.ValidateInstallationAsync(cts.Token))
                throw new InvalidOperationException("Installation auth failed");

            (rows, url, var uploadedAt) = await scriptManager.RunEnabledAsync(context, cts.Token);
            if (uploadedAt is null)
                throw new InvalidOperationException("No report was uploaded - see the script errors in the log");

            logger.LogInformation(
                "[Backfill] COMPLETE | Job={Job} | AsOf={AsOf} | {Rows} rows | {Url} | {Ms}ms",
                jobId, asOfDate.ToString("yyyy-MM-dd"), rows, url, sw.ElapsedMilliseconds);
            return (rows, url, uploadedAt.Value);
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

            sw.Stop();
            await SafeRecordAuditAsync(new ReportCycleAuditEntry(
                CycleId: cycleId,
                Trigger: context.TriggeredBy,
                RunDateWat: asOfDate,
                CycleStartWat: cycleStartWat,
                SlotLabel: "backfill",
                FacilityName: facility.FacilityName,
                DatimId: facility.DatimId,
                State: facility.State,
                AuthPassed: error != "Installation auth failed",
                Success: error.Length == 0 && url.Length > 0,
                TotalRows: rows,
                UploadUrl: url,
                Error: error,
                DurationMs: sw.ElapsedMilliseconds), CancellationToken.None);
        }
    }

    private Facility GetFacilitySafe()
    {
        try
        {
            return database.GetFacilityInfo();
        }
        catch
        {
            return new Facility
            {
                FacilityName = string.Empty,
                DatimId = string.Empty,
                State = string.Empty
            };
        }
    }

    private async Task SafeRecordAuditAsync(ReportCycleAuditEntry entry, CancellationToken ct)
    {
        try
        {
            await cycleAudit.RecordAsync(entry, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Orchestrator] Failed to record cycle audit entry.");
        }
    }
}

