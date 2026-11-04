using Cronos;
using Microsoft.Extensions.Options;

namespace AHNiRSE;

public class Worker(
    ReportOrchestrator orchestrator,
    IAuthService auth,
    IHeartbeatService heartbeat,
    FacilityRunState state,
    ForceBackfillJobStore backfillJobs,
    IOptions<ReportSettings> reportOptions,
    IOptions<HeartbeatSettings> heartbeatOptions,
    IOptions<ForceBackfillSettings> backfillOptions,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly ReportSettings _reportSettings = reportOptions.Value;
    private readonly HeartbeatSettings _heartbeatSettings = heartbeatOptions.Value;
    private readonly ForceBackfillSettings _backfillSettings = backfillOptions.Value;

    // False until the job table is checked and this machine's interrupted jobs are released.
    private bool _backfillReady;


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
            var backfillLoop = RunBackfillLoopAsync(loopToken);

            var completed = await Task.WhenAny(heartbeatLoop, reportLoop, backfillLoop);
            if (stoppingToken.IsCancellationRequested)
                break;

            logger.LogWarning("[Worker] A loop exited unexpectedly. Restarting the loops in 15 seconds.");
            loopCts.Cancel();

            try
            {
                await Task.WhenAll(heartbeatLoop, reportLoop, backfillLoop);
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
                    "[Worker] Facility identity not available at startup — " +
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
            // Service stopped before identity load completed — clean exit
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Worker] Could not load facility identity at startup — " +
                "heartbeat will skip until first report cycle completes.");
        }
    }

    // ── HEARTBEAT LOOP — runs every N minutes, always ─────────────────────────
    // Must never crash. Must never wait for the report loop.
    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _heartbeatSettings.IntervalMinutes));
        logger.LogInformation(
            "[Heartbeat] Loop started — pinging every {Interval} min.",
            _heartbeatSettings.IntervalMinutes);

        // First ping fires immediately. If identity not yet loaded, PingAsync
        // skips gracefully — no blank rows written to Table Storage.
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
                break; // service is stopping — exit cleanly
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
            // CRITICAL: this catch must exist — heartbeat loop must NEVER crash
            logger.LogWarning(
                "[Heartbeat] SafePing swallowed unexpected error: {Message}", ex.Message);
        }
    }

    // ── REPORT LOOP — cron-scheduled, heavy, independent of heartbeat ─────────
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

    // ── FORCE BACKFILL LOOP — polls BayCentral's job table, one job at a time ──
    // Independent of the report loop: a backfill may run while a scheduled cycle runs
    // (ReportOrchestrator.RunBackfillAsync decides when it must wait instead).
    private async Task RunBackfillLoopAsync(CancellationToken ct)
    {
        if (!_backfillSettings.Enabled)
        {
            logger.LogInformation("[Backfill] Disabled (ForceBackfill:Enabled=false).");
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _backfillSettings.PollMinutes));
        logger.LogInformation(
            "[Backfill] Loop started — checking for force backfill jobs every {Interval} min.",
            _backfillSettings.PollMinutes);

        while (!ct.IsCancellationRequested)
        {
            await RunPendingBackfillsAsync(ct);

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("[Backfill] Loop stopped.");
    }

    private async Task RunPendingBackfillsAsync(CancellationToken ct)
    {
        var datimId = state.Snapshot().DatimId;
        if (string.IsNullOrWhiteSpace(datimId))
            return; // facility identity not loaded yet

        try
        {
            if (!_backfillReady)
            {
                await backfillJobs.EnsureTableExistsAsync(ct);
                await backfillJobs.ReleaseInterruptedAsync(datimId, ct);
                _backfillReady = true;
            }

            while (!ct.IsCancellationRequested &&
                   await backfillJobs.TryClaimNextAsync(datimId, ct) is { } job)
            {
                logger.LogInformation(
                    "[Backfill] Claimed job {Job} (as of {AsOf}, attempt {A}/{Max}).",
                    job.RowKey, job.AsOfDate.ToString("yyyy-MM-dd"), job.Attempts, _backfillSettings.MaxAttempts);

                try
                {
                    var (rows, url, uploadedAt) = await orchestrator.RunBackfillAsync(job.AsOfDate, job.RowKey, ct);
                    await backfillJobs.CompleteAsync(job, uploadedAt, rows, url, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The job stays running; it is released the next time the loop starts.
                    _backfillReady = false;
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Backfill] Job {Job} failed: {Message}", job.RowKey, ex.Message);
                    await backfillJobs.FailAsync(job, ex.Message, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("[Backfill] Checking for jobs failed: {Message}", ex.Message);
        }
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

    // ── Schedule helpers ──────────────────────────────────────────────────────

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

