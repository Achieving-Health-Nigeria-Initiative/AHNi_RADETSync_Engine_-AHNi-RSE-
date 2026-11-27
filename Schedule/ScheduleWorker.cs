using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Schedule;

/// <summary>
/// Runs the scheduled RADET cycles (Reports:CronSchedules, WAT), plus one at startup when
/// Reports:RunOnStartup is set. Each cycle builds and uploads the workbook through
/// ReportOrchestrator, which also records Last RADET.
///
/// While the PC is blocked (its last licence check failed), it checks again every Auth:RetryWhenBlockedMinutes
/// instead of waiting for the next scheduled time, and runs a cycle as soon as BayCentral accepts it.
/// </summary>
public sealed class ScheduleWorker(
    ReportOrchestrator orchestrator,
    InstallationAuthService auth,
    IOptions<ReportSettings> options,
    IOptions<AuthSettings> authOptions,
    ILogger<ScheduleWorker> logger) : RestartingWorker(logger)
{
    private readonly ReportSettings _settings = options.Value;
    private readonly TimeSpan _blockedRetry = TimeSpan.FromMinutes(Math.Max(1,
        Math.Max(authOptions.Value.RetryWhenBlockedMinutes, authOptions.Value.BlockedCacheMinutes)));
    private static readonly TimeSpan BlockCheckStep = TimeSpan.FromMinutes(1);
    private bool _startupHandled;
    private DateTime? _announced; // report time last logged as "Next report at"

    protected override string Name => "[Schedule]";

    protected override async Task RunAsync(CancellationToken ct)
    {
        var schedule = CronSchedule.FromSettings(_settings, logger);
        logger.LogInformation(
            "[Schedule] Loop started. TimeZone='{TimeZone}', RunOnStartup={RunOnStartup}.",
            schedule.TimeZone.Id, _settings.RunOnStartup);

        // Once per service start, not again when the loop is restarted after a failure.
        if (!_startupHandled)
        {
            _startupHandled = true;
            await RunStartupAsync(schedule, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            if (schedule.Next(nowUtc) is not { } next)
            {
                logger.LogWarning("[Schedule] No future cron occurrence found. Retrying schedule scan in 60s.");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                continue;
            }

            var delay = next.NextUtc - nowUtc;
            if (auth.IsBlocked && delay > _blockedRetry)
            {
                await RetryWhileBlockedAsync(ct);
                continue;
            }

            if (next.NextUtc != _announced)
            {
                logger.LogInformation(
                    "[Schedule] Next report at {NextLocal} ({TimeZone}) via '{Cron}' in {Delay}.",
                    TimeZoneInfo.ConvertTimeFromUtc(next.NextUtc, schedule.TimeZone), schedule.TimeZone.Id, next.Cron, delay);
                _announced = next.NextUtc;
            }

            // Wait for this report time in steps of at most a minute, so a block found meanwhile (e.g. by a
            // force backfill) starts the re-checks above instead of waiting for it. The target stays fixed,
            // so a step that ends a little late never skips the report.
            var blockedMeanwhile = false;
            while (next.NextUtc - DateTime.UtcNow is var remaining && remaining > TimeSpan.Zero)
            {
                if (auth.IsBlocked && remaining > _blockedRetry)
                {
                    blockedMeanwhile = true;
                    break;
                }
                await Task.Delay(remaining < BlockCheckStep ? remaining : BlockCheckStep, ct);
            }
            if (blockedMeanwhile)
                continue;

            await RunCycleSafelyAsync(ct);
        }
    }

    /// <summary>One re-check of a blocked PC; runs the report at once if it is now accepted.</summary>
    private async Task RetryWhileBlockedAsync(CancellationToken ct)
    {
        logger.LogWarning(
            "[Schedule] This PC is blocked (not authorised in BayCentral). Checking again in {Minutes} min; the report runs as soon as it is accepted.",
            _blockedRetry.TotalMinutes);
        await Task.Delay(_blockedRetry, ct);

        bool authorised;
        try
        {
            authorised = await auth.ValidateInstallationAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("[Schedule] Licence re-check failed: {Message}", ex.Message);
            return;
        }

        if (!authorised)
            return;

        logger.LogInformation("[Schedule] This PC is now authorised - running the report now.");
        await RunCycleSafelyAsync(ct, "AuthRestored");
    }

    private async Task RunStartupAsync(CronSchedule schedule, CancellationToken ct)
    {
        if (!_settings.RunOnStartup)
            return;

        if (_settings.AllowOffScheduleStartupRun || schedule.IsDueAround(DateTime.UtcNow))
        {
            logger.LogInformation("[Schedule] Running startup report cycle.");
            await RunCycleSafelyAsync(ct);
            return;
        }

        logger.LogInformation("[Schedule] Startup cycle skipped (off-schedule). Next run will follow cron window.");
        try
        {
            // Reports the registration result early, without generating a report.
            var authorised = await auth.ValidateInstallationAsync(ct);
            logger.LogInformation("[Schedule] Startup auth pre-check result: Authorized={Authorized}.", authorised);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "[Schedule] Startup auth pre-check failed: {Message}", ex.Message);
        }
    }

    // A failed cycle is logged and the schedule carries on.
    private async Task RunCycleSafelyAsync(CancellationToken ct, string triggeredBy = "Cron")
    {
        try
        {
            await orchestrator.RunAsync(ct, triggeredBy);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "[Schedule] Unhandled error in report cycle: {Message}", ex.Message);
        }
    }
}
