using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Schedule;

/// <summary>
/// Runs the scheduled RADET cycles (Reports:CronSchedules, WAT), plus one at startup when
/// Reports:RunOnStartup is set. Each cycle builds and uploads the workbook through
/// ReportOrchestrator, which also records Last RADET.
/// </summary>
public sealed class ScheduleWorker(
    ReportOrchestrator orchestrator,
    InstallationAuthService auth,
    IOptions<ReportSettings> options,
    ILogger<ScheduleWorker> logger) : RestartingWorker(logger)
{
    private readonly ReportSettings _settings = options.Value;
    private bool _startupHandled;

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
            logger.LogInformation(
                "[Schedule] Next report at {NextLocal} ({TimeZone}) via '{Cron}' in {Delay}.",
                TimeZoneInfo.ConvertTimeFromUtc(next.NextUtc, schedule.TimeZone), schedule.TimeZone.Id, next.Cron, delay);

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            await RunCycleSafelyAsync(ct);
        }
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
    private async Task RunCycleSafelyAsync(CancellationToken ct)
    {
        try
        {
            await orchestrator.RunAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "[Schedule] Unhandled error in report cycle: {Message}", ex.Message);
        }
    }
}
