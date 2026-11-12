namespace AHNiRSE.Core.Clock;

/// <summary>
/// Single source of truth for ALL date/time values across the pipeline.
///
/// MIDNIGHT SAFETY ANALYSIS (cron: "0 */3 * * *", timezone: Africa/Lagos = WAT = UTC+1)
///
/// Potential midnight crossing scenarios:
///
///   SCENARIO A — Cycle starts 23:00 WAT, runs 30 min, finishes 23:30 WAT
///     RunDate computed at START = 2026-04-30  ← correct, both folder and file
///     No issue.
///
///   SCENARIO B — Cycle starts 23:00 WAT, runs 2h, finishes 01:00 WAT (next day)
///     RunDate computed at START = 2026-04-30  ← locked by CycleContext, still correct
///     No issue — CycleContext immutably holds the date from cycle start.
///
///   SCENARIO C — Service restarts at 00:05 WAT (RunOnStartup: true)
///     RunDate computed NOW = 2026-05-01  ← correct for the new day
///     No issue.
///
///   SCENARIO D — ResolveDates() uses DateTime.UtcNow instead of WAT
///     At 23:30 WAT = 22:30 UTC:
///       DateTime.UtcNow.Date = 2026-04-29  ← WRONG, one day behind WAT
///     Fix: ResolveDates() must receive watToday from CycleContext, not compute its own.
///
///   SCENARIO E — Worker cron scheduler uses UTC for next-occurrence
///     Cronos GetNextOccurrence(nowUtc, timeZone) correctly converts internally.
///     Lagos timezone fires at 00:00, 03:00, 06:00... WAT regardless of server TZ.
///     No issue if Cronos library is used correctly (see Worker.cs).
///
/// RULE: Nothing in the pipeline calls DateTime.Now, DateTime.Today, or DateTime.UtcNow
///       for date-labelling purposes. All date values come from IReportClock, injected
///       once at cycle start into CycleContext and passed down immutably.
/// </summary>
public interface IReportClock
{
    /// <summary>Returns current DateTimeOffset in West Africa Time (UTC+1).</summary>
    DateTimeOffset NowInWat();

    /// <summary>
    /// Returns today's date in WAT.
    /// Use ONLY in ReportOrchestrator at cycle start — store result in CycleContext.RunDate.
    /// Do not call this anywhere else.
    /// </summary>
    DateOnly TodayInWat();

    /// <summary>
    /// Returns current UTC DateTime.
    /// Use ONLY for scheduling calculations (Cronos next-occurrence).
    /// Never use for date-labelling files or folders.
    /// </summary>
    DateTime UtcNow();
}
