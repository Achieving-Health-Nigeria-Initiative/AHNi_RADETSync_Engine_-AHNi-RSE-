using Cronos;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Schedule;

/// <summary>
/// The report schedule: Reports:CronSchedules (or the single Reports:CronSchedule) in
/// Reports:TimeZoneId (Africa/Lagos = WAT). Invalid cron lines are logged and skipped.
/// </summary>
public sealed class CronSchedule
{
    private readonly List<Entry> _entries;

    public TimeZoneInfo TimeZone { get; }

    private CronSchedule(TimeZoneInfo timeZone, List<Entry> entries)
    {
        TimeZone = timeZone;
        _entries = entries;
    }

    /// <summary>Throws when no cron line is valid.</summary>
    public static CronSchedule FromSettings(ReportSettings settings, ILogger logger)
    {
        var raw = settings.CronSchedules
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();
        if (raw.Count == 0)
            raw.Add(settings.CronSchedule.Trim());

        var entries = new List<Entry>();
        foreach (var cron in raw.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                entries.Add(new Entry(cron, CronExpression.Parse(cron, CronFormat.Standard)));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Schedule] Invalid cron '{Cron}'.", cron);
            }
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("No valid cron schedules found.");

        return new CronSchedule(ResolveTimeZone(settings.TimeZoneId, logger), entries);
    }

    /// <summary>The next run after <paramref name="nowUtc"/> and the cron line that produces it.</summary>
    public (DateTime NextUtc, string Cron)? Next(DateTime nowUtc)
    {
        (DateTime NextUtc, string Cron)? best = null;
        foreach (var entry in _entries)
        {
            var next = entry.Expression.GetNextOccurrence(nowUtc, TimeZone);
            if (next is not null && (best is null || next.Value < best.Value.NextUtc))
                best = (next.Value, entry.Cron);
        }
        return best;
    }

    /// <summary>True when a run falls between a minute ago and 5 seconds from now.</summary>
    public bool IsDueAround(DateTime nowUtc)
    {
        var windowStart = nowUtc.AddMinutes(-1);
        var windowEnd = nowUtc.AddSeconds(5);
        return _entries
            .Select(e => e.Expression.GetNextOccurrence(windowStart, TimeZone))
            .Any(next => next is not null && next.Value >= windowStart && next.Value <= windowEnd);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            logger.LogWarning("[Schedule] TimeZoneId not set. Using local: '{TimeZone}'.", TimeZoneInfo.Local.Id);
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("[Schedule] TimeZone '{Id}' not found or invalid. Using local.", timeZoneId);
            return TimeZoneInfo.Local;
        }
    }

    private sealed record Entry(string Cron, CronExpression Expression);
}
