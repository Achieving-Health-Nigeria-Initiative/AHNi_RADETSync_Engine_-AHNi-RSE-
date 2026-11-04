namespace AHNiRSE.Platform;

/// <summary>
/// Single source of truth for report dates: today and now in West Africa Time (UTC+1).
///
/// The WAT date is read ONCE, at cycle start in ReportOrchestrator, and carried in CycleContext, so
/// a cycle that crosses midnight still files its workbook under the day it started, and the
/// reporting period uses WAT rather than UTC (at 23:30 WAT the UTC date is still the day before).
/// Nothing else may use DateTime.Now / Today / UtcNow to date a report, folder or file.
/// (Cron scheduling works in UTC with the time zone applied by Cronos - see Schedule/CronSchedule.)
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
}
