using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Backfill;

/// <summary>
/// Runs the force backfills requested in BayCentral: every ForceBackfill:PollMinutes it claims this
/// facility's pending jobs one at a time, oldest first, runs each through
/// ReportOrchestrator.RunBackfillAsync (which decides whether it may overlap a scheduled cycle) and
/// records the result on the job row. A job's ReportType picks what it produces: the RADET workbook
/// (default) or the raw report (raw, see RawReport).
///
/// Every poll also releases jobs left running by a machine that never came back (see
/// ForceBackfillJobStore.ReleaseAbandonedAsync), so a job taken by a retired or renamed PC is not
/// stuck forever.
/// </summary>
public sealed class BackfillWorker(
    ReportOrchestrator orchestrator,
    ForceBackfillJobStore jobs,
    FacilityRunState state,
    IOptions<ForceBackfillSettings> options,
    IOptions<ReportSettings> reportOptions,
    ILogger<BackfillWorker> logger) : RestartingWorker(logger)
{
    private readonly ForceBackfillSettings _settings = options.Value;

    // A job's run is cut off after Reports:GlobalCycleTimeoutSeconds, and before that it may wait up to
    // one scheduled cycle for the cycle lock, so a live machine's job never stays running for more than
    // about twice that. Three times it means the machine that picked it up is gone.
    private readonly TimeSpan _abandonedAfter =
        TimeSpan.FromSeconds(3 * Math.Max(30, reportOptions.Value.GlobalCycleTimeoutSeconds));

    // False until the job table is checked and this machine's interrupted jobs are released.
    private bool _ready;

    protected override string Name => "[Backfill]";

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!_settings.Enabled)
        {
            logger.LogInformation("[Backfill] Disabled (ForceBackfill:Enabled=false).");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PollMinutes));
        logger.LogInformation(
            "[Backfill] Loop started — checking for force backfill jobs every {Interval} min.", _settings.PollMinutes);

        while (!ct.IsCancellationRequested)
        {
            await RunPendingJobsAsync(ct);
            await Task.Delay(interval, ct);
        }
    }

    private async Task RunPendingJobsAsync(CancellationToken ct)
    {
        var datimId = state.Snapshot().DatimId;
        if (string.IsNullOrWhiteSpace(datimId))
            return; // facility identity not loaded yet

        try
        {
            if (!_ready)
            {
                await jobs.EnsureTableExistsAsync(ct);
                await jobs.ReleaseInterruptedAsync(datimId, ct);
                _ready = true;
            }

            await jobs.ReleaseAbandonedAsync(datimId, _abandonedAfter, ct);

            while (!ct.IsCancellationRequested && await jobs.TryClaimNextAsync(datimId, ct) is { } job)
            {
                logger.LogInformation(
                    "[Backfill] Claimed job {Job} ({Type}, as of {AsOf}, attempt {A}/{Max}).",
                    job.RowKey, job.ReportType, job.AsOfDate.ToString("yyyy-MM-dd"), job.Attempts, _settings.MaxAttempts);

                try
                {
                    var result = await orchestrator.RunBackfillAsync(job.AsOfDate, job.RowKey, job.ReportType, ct);
                    await jobs.CompleteAsync(job, result, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The job stays running; it is released the next time the loop starts.
                    _ready = false;
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Backfill] Job {Job} failed: {Message}", job.RowKey, ex.Message);
                    await jobs.FailAsync(job, ex.Message, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("[Backfill] Checking for jobs failed: {Message}", ex.Message);
        }
    }
}
