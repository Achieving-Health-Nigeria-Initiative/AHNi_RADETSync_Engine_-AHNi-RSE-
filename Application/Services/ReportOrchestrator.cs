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
    ILogger<ReportOrchestrator> logger)
{
    private readonly ReportSettings _settings = reportOptions.Value;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

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
            logger.LogWarning("[Orchestrator] Skipped - previous cycle still running.");
            return (0, string.Empty);
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

            var (rows, url) = await scriptManager.RunEnabledAsync(context, cts.Token);
            sw.Stop();

            heartbeat.SetReportSuccess(
                facility.FacilityName,
                facility.DatimId,
                facility.State,
                DateTimeOffset.UtcNow,
                rows,
                url);

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

