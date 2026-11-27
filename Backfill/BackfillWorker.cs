using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Backfill;

/// <summary>
/// Runs the force backfills requested in BayCentral: every ForceBackfill:PollMinutes it claims this
/// facility's pending jobs one at a time, oldest first, runs each through
/// ReportOrchestrator.RunBackfillAsync (which decides whether it may overlap a scheduled cycle) and
/// records the result on the job row. A job's ReportType picks what it produces: the RADET workbook
/// (default), the raw report (raw, see RawReport) or a custom SQL script's result (custom, see CustomQuery).
///
/// Every poll also releases jobs left running by a machine that never came back (see
/// ForceBackfillJobStore.ReleaseAbandonedAsync), so a job taken by a retired or renamed PC is not
/// stuck forever.
///
/// While this PC is blocked by the licence check (not registered in BayCentral) jobs are left pending, and a job
/// the check stops goes back to pending without using an attempt. They run as soon as the PC is accepted: the
/// wait between polls ends early when that happens (ScheduleWorker re-checks every few minutes while blocked).
/// </summary>
public sealed class BackfillWorker(
    ReportOrchestrator orchestrator,
    ForceBackfillJobStore jobs,
    InstallationAuthService auth,
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

    // Logged once per blocked spell, not every poll.
    private bool _toldBlocked;

    private const string WaitingForAuth =
        "Waiting: this PC is not authorised in BayCentral (Serial Numbers). Runs once it is accepted.";

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
            await WaitForNextPollAsync(interval, ct);
        }
    }

    /// <summary>Waits one poll interval, or less if this PC was blocked and is accepted meanwhile.</summary>
    private async Task WaitForNextPollAsync(TimeSpan interval, CancellationToken ct)
    {
        if (!auth.IsBlocked)
        {
            await Task.Delay(interval, ct);
            return;
        }

        var until = DateTime.UtcNow + interval;
        while (DateTime.UtcNow < until && auth.IsBlocked)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        if (!auth.IsBlocked)
            logger.LogInformation("[Backfill] This PC is now authorised - checking for waiting jobs now.");
    }

    private async Task RunPendingJobsAsync(CancellationToken ct)
    {
        var datimId = state.Snapshot().DatimId;
        if (string.IsNullOrWhiteSpace(datimId))
            return; // facility identity not loaded yet
        if (orchestrator.UpdatePending)
            return; // a new version is being installed; jobs are picked up after the restart

        try
        {
            if (!_ready)
            {
                await jobs.EnsureTableExistsAsync(ct);
                await jobs.ReleaseInterruptedAsync(datimId, ct);
                _ready = true;
            }

            await jobs.ReleaseAbandonedAsync(datimId, _abandonedAfter, ct);

            if (auth.IsBlocked)
            {
                if (!_toldBlocked)
                    logger.LogWarning("[Backfill] This PC is blocked (not authorised in BayCentral) - pending jobs wait until it is accepted.");
                _toldBlocked = true;
                return;
            }
            _toldBlocked = false;

            while (!ct.IsCancellationRequested && await jobs.TryClaimNextAsync(datimId, ct) is { } job)
            {
                logger.LogInformation(
                    "[Backfill] Claimed job {Job} ({Type}, as of {AsOf}, attempt {A}/{Max}).",
                    job.RowKey, job.ReportType, job.AsOfDate.ToString("yyyy-MM-dd"), job.Attempts, _settings.MaxAttempts);

                try
                {
                    var result = await orchestrator.RunBackfillAsync(job.AsOfDate, job.RowKey, job.ReportType, job.Custom, ct);
                    await jobs.CompleteAsync(job, result, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The job stays running; it is released the next time the loop starts.
                    _ready = false;
                    return;
                }
                catch (InstallationNotAuthorisedException)
                {
                    logger.LogWarning(
                        "[Backfill] Job {Job} waits - this PC is not authorised in BayCentral. Back to pending (attempt not counted); it runs once the PC is accepted.",
                        job.RowKey);
                    await jobs.ReturnToPendingAsync(job, WaitingForAuth, ct);
                    _toldBlocked = true;
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
