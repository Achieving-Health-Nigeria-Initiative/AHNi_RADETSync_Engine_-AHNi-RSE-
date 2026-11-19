namespace AHNiRSE.Reports;

/// <summary>
/// The :start_date / :end_date each sheet's query runs with.
///
/// The end date is the Reports:Scripts entry's EndDate, or the cycle's date (today in WAT, or the
/// as-of date of a force backfill). The start date is the entry's StartDate, or the sheet's default
/// worked out from that end date. A force backfill ignores the entry's dates and always uses the
/// defaults, so a backfill for 30 Sep 2026 gets 30 Sep 2026 as every sheet's end date.
/// Dates come only from CycleContext — never DateTime.UtcNow / Today.
/// </summary>
public static class ReportPeriods
{
    /// <summary>RADET, PMTCT HTS, Maternal Cohort, Prep and AHD default start: the whole cohort.</summary>
    public static readonly DateOnly CohortStart = new(1980, 1, 1);

    /// <summary>A sheet whose default start does not depend on the end date.</summary>
    public static (DateOnly Start, DateOnly End) Resolve(ScriptConfig? cfg, CycleContext context, DateOnly defaultStart)
        => Resolve(cfg, context, _ => defaultStart);

    public static (DateOnly Start, DateOnly End) Resolve(
        ScriptConfig? cfg,
        CycleContext context,
        Func<DateOnly, DateOnly> defaultStart)
    {
        if (context.Backfill)
            cfg = null;

        var end = string.IsNullOrWhiteSpace(cfg?.EndDate) ? context.WatToday : DateOnly.Parse(cfg.EndDate);
        var start = string.IsNullOrWhiteSpace(cfg?.StartDate) ? defaultStart(end) : DateOnly.Parse(cfg.StartDate);

        if (start > end)
            throw new ArgumentException($"StartDate {start} > EndDate {end}");

        return (start, end);
    }

    /// <summary>
    /// HTS default start: 1 July before the PEPFAR fiscal year (1 October - 30 September) that contains
    /// <paramref name="endDate"/>, i.e. the fiscal year plus the three months before it - the window the
    /// previous blob hts.sql used, but taken from the end date so a past period (EndDate 2025-09-30 ->
    /// 2024-07-01) gets that year's window.
    /// </summary>
    public static DateOnly HtsStart(DateOnly endDate)
    {
        var fiscalYearStart = new DateOnly(endDate.Month >= 10 ? endDate.Year : endDate.Year - 1, 10, 1);
        return fiscalYearStart.AddMonths(-3);
    }
}
