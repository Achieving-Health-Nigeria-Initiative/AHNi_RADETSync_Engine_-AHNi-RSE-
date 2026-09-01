using AHNiRSE.Core.Clock;

namespace AHNiRSE.Infrastructure.Clock;

/// <summary>
/// Production clock — always returns real system time in WAT (UTC+1).
///
/// Lagos / Nigeria Standard Time = UTC+1, fixed offset, no DST ever.
///
/// Uses TimeZoneInfo.FindSystemTimeZoneById("Africa/Lagos") on Linux/macOS
/// and "W. Central Africa Standard Time" on Windows as fallback — both resolve
/// to UTC+1 fixed. Falls back to manual CreateCustomTimeZone if neither exists
/// (e.g. stripped Docker images without timezone data).
///
/// Thread-safe: all methods are stateless.
/// </summary>
public sealed class WatClock : IReportClock
{
    private static readonly TimeZoneInfo Wat = ResolveWat();

    public DateTimeOffset NowInWat()
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Wat);

    public DateOnly TodayInWat()
        => DateOnly.FromDateTime(NowInWat().DateTime);

    public DateTime UtcNow()
        => DateTime.UtcNow;

    // ── WAT timezone resolution with fallback chain ───────────────────────────
    private static TimeZoneInfo ResolveWat()
    {
        // Try IANA name (Linux, macOS, Windows with tzdata)
        if (TryFind("Africa/Lagos", out var tz)) return tz!;

        // Try Windows name (bare Windows without IANA support)
        if (TryFind("W. Central Africa Standard Time", out tz)) return tz!;

        // Final fallback: create UTC+1 fixed-offset zone manually
        // This is safe for Nigeria because WAT has NO daylight saving time.
        return TimeZoneInfo.CreateCustomTimeZone(
            id: "WAT-manual",
            baseUtcOffset: TimeSpan.FromHours(1),
            displayName: "(UTC+01:00) West Africa Time",
            standardDisplayName: "West Africa Time");
    }

    private static bool TryFind(string id, out TimeZoneInfo? result)
    {
        try { result = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch { result = null; return false; }
    }
}
