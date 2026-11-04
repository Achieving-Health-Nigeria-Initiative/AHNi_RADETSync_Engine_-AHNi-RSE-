using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Heartbeat;

/// <summary>
/// Pings the FacilityHeartbeat table every Heartbeat:IntervalMinutes so BayCentral can tell the
/// facility is online. Runs independently of report cycles, so a long or failing report never
/// stops the heartbeat.
///
/// At start it makes sure the table exists and loads the facility's identity (name, DATIM ID,
/// state) from the database; until that identity is known, pings are skipped rather than writing
/// a row without a DATIM ID.
/// </summary>
public sealed class HeartbeatWorker(
    AzureHeartbeatService heartbeat,
    FacilityRunState state,
    IDatabaseService database,
    IOptions<HeartbeatSettings> options,
    ILogger<HeartbeatWorker> logger) : RestartingWorker(logger)
{
    private readonly HeartbeatSettings _settings = options.Value;

    protected override string Name => "[Heartbeat]";

    protected override async Task RunAsync(CancellationToken ct)
    {
        await heartbeat.EnsureTableExistsAsync(ct);
        LoadFacilityIdentity();

        var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.IntervalMinutes));
        logger.LogInformation("[Heartbeat] Loop started — pinging every {Interval} min.", _settings.IntervalMinutes);

        while (!ct.IsCancellationRequested)
        {
            await SafePingAsync(ct);

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void LoadFacilityIdentity()
    {
        if (state.HasIdentity)
            return;

        try
        {
            var facility = database.GetFacilityInfo();
            if (string.IsNullOrWhiteSpace(facility.DatimId))
            {
                logger.LogWarning(
                    "[Heartbeat] Facility DATIM ID not found in the database — pings wait for the first report cycle.");
                return;
            }

            state.SetFacilityIdentity(facility.FacilityName, facility.DatimId, facility.State);
            logger.LogInformation(
                "[Heartbeat] Facility identity loaded: {FacilityName} ({DatimId}) [{State}]",
                facility.FacilityName, facility.DatimId, facility.State);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Heartbeat] Could not load facility identity — pings wait for the first report cycle.");
        }
    }

    // A ping must never end the loop.
    private async Task SafePingAsync(CancellationToken ct)
    {
        try
        {
            await heartbeat.PingAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("[Heartbeat] Ping failed unexpectedly: {Message}", ex.Message);
        }
    }
}
