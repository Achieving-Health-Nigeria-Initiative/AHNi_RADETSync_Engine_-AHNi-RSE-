namespace AHNiRSE.Platform;

/// <summary>Folders the service writes to, next to its exe so everything is in one place.</summary>
public static class AppPaths
{
    /// <summary>
    /// &lt;install folder&gt;\logs: the daily log files (ahni-rse-&lt;date&gt;.log) and the cycle audit
    /// (cycle-audit.jsonl). Under the service install this is C:\Services\AHNi-RSE\logs.
    /// </summary>
    public static string Logs => Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>The last facility type BayCentral gave (care / genpop), used when it cannot be reached.</summary>
    public static string FacilityProfile => Path.Combine(AppContext.BaseDirectory, "facility-profile.json");
}
