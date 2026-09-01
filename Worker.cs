using AHNiRSE.Application.Services;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Infrastructure.Heartbeat;
using AHNiRSE.Shared;
using Cronos;
using Microsoft.Extensions.Options;

namespace AHNiRSE;

public class Worker(
    ReportOrchestrator orchestrator,
    IAuthService auth,
    IHeartbeatService heartbeat,
    FacilityRunState state,
    IOptions<ReportSettings> reportOptions,
    IOptions<HeartbeatSettings> heartbeatOptions,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly ReportSettings _reportSettings = reportOptions.Value;
    private readonly HeartbeatSettings _heartbeatSettings = heartbeatOptions.Value;

 #if false
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeZoneInfo timeZone;
        List<ScheduleEntry> schedules;

        try
        {
            timeZone = ResolveTimeZone(_reportSettings.TimeZoneId);
            schedules = ResolveSchedules();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Worker] Failed to initialize scheduling. Service will stop.");
            return;
        }

        // Ensure heartbeat table exists before loops start
        if (heartbeat is AzureHeartbeatService azureHb)
            await azureHb.EnsureTableExistsAsync(stoppingToken);

        // â”€â”€ Pre-load facility identity so the first heartbeat ping has real data â”€â”€
        // If this fails or returns no identity, PingAsync will skip safely until
        // ReportOrchestrator populates FacilityRunState after the first report cycle.
        await LoadFacilityIdentityAsync(stoppingToken);

        logger.LogInformation(
            "[Worker] Started. TimeZone='{TimeZone}', RunOnStartup={RunOnStartup}, " +
            "HeartbeatInterval={Interval}min, IdentityReady={IdentityReady}",
            timeZone.Id, _reportSettings.RunOnStartup,
            _heartbeatSettings.IntervalMinutes, state.HasIdentity);

        // â”€â”€ Two fully independent loops â€” neither awaits the other â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var heartbeatLoop = RunHeartbeatLoopAsync(stoppingToken);
        var reportLoop = RunReportLoopAsync(timeZone, schedules, stoppingToken);

        await Task.WhenAll(heartbeatLoop, reportLoop);

        logger.LogInformation("[Worker] Both loops stopped. Service shutting down.");
    }

    // â”€â”€ STARTUP IDENTITY LOAD â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Runs once before the loops start. Loads facility name + DatimId from the
    // orchestrator so the very first heartbeat ping uses the real PartitionKey
    // instead of writing a blank DESKTOP-XXXX row to Azure Table Storage.
    //
    // If it fails for any reason (DB offline, config missing, etc.) the service
    // still starts â€” PingAsync guards on HasIdentity and skips safely until the
    // first report cycle populates FacilityRunState via ReportOrchestrator.
 #endif

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeZoneInfo timeZone;
            List<ScheduleEntry> schedules;

            try
            {
                timeZone = ResolveTimeZone(_reportSettings.TimeZoneId);
                schedules = ResolveSchedules();
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[Worker] Failed to initialize scheduling. Retrying in 1 minute.");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (heartbeat is AzureHeartbeatService azureHb)
                await azureHb.EnsureTableExistsAsync(stoppingToken);

            await LoadFacilityIdentityAsync(stoppingToken);

            logger.LogInformation(
                "[Worker] Supervisor active. TimeZone='{TimeZone}', RunOnStartup={RunOnStartup}, HeartbeatInterval={Interval}min, IdentityReady={IdentityReady}",
                timeZone.Id, _reportSettings.RunOnStartup, _heartbeatSettings.IntervalMinutes, state.HasIdentity);

            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loopToken = loopCts.Token;
            var heartbeatLoop = RunHeartbeatLoopAsync(loopToken);
            var reportLoop = RunReportLoopAsync(timeZone, schedules, loopToken);

            var completed = await Task.WhenAny(heartbeatLoop, reportLoop);
            if (stoppingToken.IsCancellationRequested)
                break;

            logger.LogWarning("[Worker] A loop exited unexpectedly. Restarting both loops in 15 seconds.");
            loopCts.Cancel();

            try
            {
                await Task.WhenAll(heartbeatLoop, reportLoop);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "[Worker] Loop ended with error. Supervisor will restart.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("[Worker] Service loop stopped.");
    }

    private async Task LoadFacilityIdentityAsync(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("[Worker] Loading facility identity from database...");

            var (facilityName, datimId, facilityState) = await orchestrator.LoadFacilityIdentityAsync(ct);

            if (string.IsNullOrWhiteSpace(datimId))
            {
                logger.LogWarning(
                    "[Worker] Facility identity not available at startup â€” " +
                    "heartbeat will skip until first report cycle completes.");
                return;
            }

            state.SetFacilityIdentity(facilityName, datimId, facilityState);

            logger.LogInformation(
                "[Worker] Facility identity loaded: {FacilityName} ({DatimId}) [{State}]",
                facilityName, datimId, facilityState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Service stopped before identity load completed â€” clean exit
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Worker] Could not load facility identity at startup â€” " +
                "heartbeat will skip until first report cycle completes.");
        }
    }

    // â”€â”€ HEARTBEAT LOOP â€” runs every N minutes, always â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Must never crash. Must never wait for the report loop.
    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _heartbeatSettings.IntervalMinutes));
        logger.LogInformation(
            "[Heartbeat] Loop started â€” pinging every {Interval} min.",
            _heartbeatSettings.IntervalMinutes);

        // First ping fires immediately. If identity not yet loaded, PingAsync
        // skips gracefully â€” no blank rows written to Table Storage.
        await SafePingAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    interval,
                    ct);
            }
            catch (OperationCanceledException)
            {
                break; // service is stopping â€” exit cleanly
            }

            await SafePingAsync(ct);
        }

        logger.LogInformation("[Heartbeat] Loop stopped.");
    }

    private async Task SafePingAsync(CancellationToken ct)
    {
        try
        {
            await heartbeat.PingAsync(ct);
        }
        catch (Exception ex)
        {
            // CRITICAL: this catch must exist â€” heartbeat loop must NEVER crash
            logger.LogWarning(
                "[Heartbeat] SafePing swallowed unexpected error: {Message}", ex.Message);
        }
    }

    // â”€â”€ REPORT LOOP â€” cron-scheduled, heavy, independent of heartbeat â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private async Task RunReportLoopAsync(
        TimeZoneInfo timeZone,
        List<ScheduleEntry> schedules,
        CancellationToken ct)
    {
        if (_reportSettings.RunOnStartup && !ct.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            if (_reportSettings.AllowOffScheduleStartupRun ||
                IsWithinScheduleWindow(schedules, nowUtc, timeZone))
            {
                logger.LogInformation("[Worker] Running startup report cycle.");
                await RunReportCycleSafelyAsync(ct);
            }
            else
            {
                logger.LogInformation(
                    "[Worker] Startup cycle skipped (off-schedule). Next run will follow cron window.");
                await RunStartupAuthProbeAsync(ct);
            }
        }

        while (!ct.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var next = GetNextOccurrence(schedules, nowUtc, timeZone);

            if (next is null)
            {
                logger.LogWarning("[Worker] No future cron occurrence found. Retrying schedule scan in 60s.");
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            var delay = next.Value.NextUtc - nowUtc;
            var nextLocal = TimeZoneInfo.ConvertTimeFromUtc(next.Value.NextUtc, timeZone);

            logger.LogInformation(
                "[Worker] Next report at {NextLocal} ({TimeZone}) via '{Cron}' in {Delay}.",
                nextLocal, timeZone.Id, next.Value.Original, delay);

            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }
            }

            if (!ct.IsCancellationRequested)
                await RunReportCycleSafelyAsync(ct);
        }

        logger.LogInformation("[Worker] Report loop stopped.");
    }

    private async Task RunReportCycleSafelyAsync(CancellationToken ct)
    {
        try
        {
            await orchestrator.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Worker] Unhandled error in report cycle: {Message}", ex.Message);
        }
    }

    private async Task RunStartupAuthProbeAsync(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("[Worker] Running startup auth pre-check (no report upload).");
            var isAuthorized = await auth.ValidateInstallationAsync(ct);
            logger.LogInformation(
                "[Worker] Startup auth pre-check result: Authorized={Authorized}.",
                isAuthorized);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Worker] Startup auth pre-check failed: {Message}", ex.Message);
        }
    }

    // â”€â”€ Schedule helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private List<ScheduleEntry> ResolveSchedules()
    {
        var rawSchedules = _reportSettings.CronSchedules
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

        if (rawSchedules.Count == 0)
            rawSchedules.Add(_reportSettings.CronSchedule.Trim());

        var parsed = new List<ScheduleEntry>();
        foreach (var raw in rawSchedules.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                parsed.Add(new ScheduleEntry(raw, CronExpression.Parse(raw, CronFormat.Standard)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Worker] Invalid cron '{Cron}'.", raw);
            }
        }

        if (parsed.Count == 0)
            throw new InvalidOperationException("No valid cron schedules found.");

        return parsed;
    }

    private static NextSchedule? GetNextOccurrence(
        IReadOnlyCollection<ScheduleEntry> schedules,
        DateTime nowUtc,
        TimeZoneInfo timeZone)
    {
        NextSchedule? best = null;
        foreach (var schedule in schedules)
        {
            var nextUtc = schedule.Expression.GetNextOccurrence(nowUtc, timeZone);
            if (nextUtc is null) continue;
            if (best is null || nextUtc.Value < best.Value.NextUtc)
                best = new NextSchedule(schedule.Original, nextUtc.Value);
        }
        return best;
    }

    private static bool IsWithinScheduleWindow(
        IReadOnlyCollection<ScheduleEntry> schedules,
        DateTime nowUtc,
        TimeZoneInfo timeZone)
    {
        var windowStart = nowUtc.AddMinutes(-1);
        var windowEnd = nowUtc.AddSeconds(5);

        foreach (var schedule in schedules)
        {
            var next = schedule.Expression.GetNextOccurrence(windowStart, timeZone);
            if (next is null)
            {
                continue;
            }

            if (next.Value >= windowStart && next.Value <= windowEnd)
            {
                return true;
            }
        }

        return false;
    }

    private TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            logger.LogWarning(
                "[Worker] TimeZoneId not set. Using local: '{TimeZone}'.",
                TimeZoneInfo.Local.Id);
            return TimeZoneInfo.Local;
        }

        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning("[Worker] TimeZone '{Id}' not found. Using local.", timeZoneId);
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            logger.LogWarning("[Worker] TimeZone '{Id}' invalid. Using local.", timeZoneId);
            return TimeZoneInfo.Local;
        }
    }

    private readonly record struct ScheduleEntry(string Original, CronExpression Expression);
    private readonly record struct NextSchedule(string Original, DateTime NextUtc);
}

