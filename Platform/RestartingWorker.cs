using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Platform;

/// <summary>
/// A background loop that is restarted 15 seconds after it fails or returns, until the service stops.
///
/// The host runs with BackgroundServiceExceptionBehavior.Ignore, so an exception escaping a plain
/// BackgroundService would end that loop silently while the service keeps running. Each of the
/// service's loops (heartbeat, schedule, force backfill) derives from this instead, so one failing
/// never stops the others and never stays stopped.
/// </summary>
public abstract class RestartingWorker(ILogger logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(15);

    /// <summary>Log prefix, e.g. "[Heartbeat]".</summary>
    protected abstract string Name { get; }

    /// <summary>The loop itself. Returning before the service stops counts as a failure.</summary>
    protected abstract Task RunAsync(CancellationToken ct);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested)
                    break;
                logger.LogWarning("{Name} Loop exited unexpectedly. Restarting in {Delay}s.", Name, RestartDelay.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Name} Loop failed. Restarting in {Delay}s.", Name, RestartDelay.TotalSeconds);
            }

            try { await Task.Delay(RestartDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("{Name} Loop stopped.", Name);
    }
}
