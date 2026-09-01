using AHNiRSE.Infrastructure;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Core;

public class CycleOrchestrator
{
    private readonly CombinedReportJob _job;
    private readonly ILogger<CycleOrchestrator> _logger;

    public CycleOrchestrator(
        CombinedReportJob job,
        ILogger<CycleOrchestrator> logger)
    {
        _job = job;
        _logger = logger;
    }

    public async Task RunCycleAsync(DateOnly reportDate, CancellationToken ct)
    {
        var cycleId = Guid.NewGuid();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("Cycle {CycleId} started — all facilities — report date {Date}",
            cycleId, reportDate);

        await _job.RunAsync(cycleId, reportDate, ct);

        sw.Stop();
        RseMetrics.LastCycleDurationSeconds.Set(sw.Elapsed.TotalSeconds);
        RseMetrics.LastCycleTimestamp.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        _logger.LogInformation("Cycle {CycleId} completed in {Elapsed:F1}s", cycleId, sw.Elapsed.TotalSeconds);
    }
}