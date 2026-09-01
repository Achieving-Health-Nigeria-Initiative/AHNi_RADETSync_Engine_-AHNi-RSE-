using System.Text.RegularExpressions;

namespace AHNiRSE.Export;

public static class FacilityFilenameBuilder
{
    private static readonly Regex InvalidChars = new(@"['\.,]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Per-facility filename — kept for reference but no longer used in main flow.</summary>
    public static string Build(string facilityName, DateOnly reportDate)
    {
        var cleaned = InvalidChars.Replace(facilityName, "");
        var underscored = MultiSpace.Replace(cleaned.Trim(), "_").Replace(" ", "_");
        return $"{underscored}_{reportDate:yyyy_MM_dd}.xlsx";
    }

    /// <summary>Single combined file covering all active facilities.</summary>
    public static string BuildCombined(DateOnly reportDate)
        => $"ACE-1_Combined_RADET-{reportDate:yyyy-MM-dd}.xlsx";
}