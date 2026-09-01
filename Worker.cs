using AHNiRSE.Configuration;
using AHNiRSE.Core;
using Cronos;
using Microsoft.Extensions.Options;

namespace AHNiRSE;

public class Worker : BackgroundService
{
    private readonly CycleOrchestrator _orchestrator;
    private readonly ReportSettings _settings;
    private readonly ILogger<Worker> _logger;

    public Worker(
        CycleOrchestrator orchestrator,
        IOptions<ReportSettings> settings,
        ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _settings     = settings.Value;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cron = CronExpression.Parse(_settings.CronSchedule);
        var tz   = TimeZoneInfo.FindSystemTimeZoneById(_settings.TimeZoneId);

        _logger.LogInformation("AHNi-RSE started — schedule: {Cron} ({Tz})", _settings.CronSchedule, tz.DisplayName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now  = DateTimeOffset.UtcNow;
            var next = cron.GetNextOccurrence(now, tz);

            if (next is null)
            {
                _logger.LogWarning("No next occurrence for cron — sleeping 1 hour");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            var delay   = next.Value - now;
            var nextWat = TimeZoneInfo.ConvertTime(next.Value, tz);
            _logger.LogInformation("Next run at {Next} (in {Delay:hh\\:mm\\:ss})", nextWat, delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            var reportDate = DateOnly.FromDateTime(DateTime.Today);
            _logger.LogInformation("Triggering cycle for report date {Date}", reportDate);

            try
            {
                await _orchestrator.RunCycleAsync(reportDate, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in cycle — will retry next scheduled run");
            }
        }
    }
}
