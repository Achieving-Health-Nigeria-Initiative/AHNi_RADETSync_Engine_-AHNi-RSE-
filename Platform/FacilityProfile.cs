using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Platform;

/// <summary>
/// Whether this facility is a community centre ("care") or a gen-pop facility - set in appsettings.json
/// (Facility:IsCommunityCentre, false = gen-pop by default) and nowhere else. It decides where workbooks go
/// and what they hold:
///   community centre → Storage:Azure:CareContainerName (community-center), named Reports:CareFileNameFormat
///                      ({facility}_{datim}_{date}), without Facility:CareExcludedSheets (PMTCT HTS and
///                      Maternal Cohort by default)
///   gen-pop          → Storage:Azure:ContainerName (autoreport), named Reports:FileNameFormat
///                      ({facility}_{date}), all sheets.
/// The release script builds a gen-pop package and a community-centre package that differ only in this setting.
/// </summary>
public sealed class FacilityProfile(
    IOptions<ReportSettings> reportOptions,
    IOptions<StorageSettings> storageOptions,
    IOptions<FacilitySettings> facilityOptions)
{
    private readonly ReportSettings _reports = reportOptions.Value;
    private readonly AzureStorageOptions _storage = storageOptions.Value.Azure;
    private readonly FacilitySettings _facility = facilityOptions.Value;

    /// <summary>True for a community centre (Facility:IsCommunityCentre).</summary>
    public bool IsCare => _facility.IsCommunityCentre;

    /// <summary>Left out for community centres when Facility:CareExcludedSheets is not set.</summary>
    public static readonly string[] DefaultCareExcludedSheets = ["PMTCTHTS", "Maternal"];

    private static readonly IReadOnlySet<string> NoSheets = new HashSet<string>();

    /// <summary>
    /// Reports:Scripts names of the sheets this facility leaves out: Facility:CareExcludedSheets for a
    /// community centre ([ "none" ] = none), nothing for gen-pop. RADET is never left out.
    /// </summary>
    public IReadOnlySet<string> ExcludedSheets
    {
        get
        {
            if (!IsCare)
                return NoSheets;
            var names = _facility.CareExcludedSheets is { Length: > 0 } configured ? configured : DefaultCareExcludedSheets;
            return names
                .Select(n => n?.Trim() ?? string.Empty)
                .Where(n => n.Length > 0
                            && !n.Equals("none", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals(ReportScripts.Radet, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when this facility leaves the Reports:Scripts entry <paramref name="scriptName"/> out.</summary>
    public bool Excludes(string scriptName) => ExcludedSheets.Contains(scriptName);

    /// <summary>Container finished workbooks are uploaded to.</summary>
    public string UploadContainer => IsCare && !string.IsNullOrWhiteSpace(_storage.CareContainerName)
        ? _storage.CareContainerName
        : _storage.ContainerName;

    /// <summary>Folder in UploadContainer the workbook goes to (Reports:WorkbookFolder, RADET by default).</summary>
    public string WorkbookFolder => string.IsNullOrWhiteSpace(_reports.WorkbookFolder)
        ? "RADET"
        : _reports.WorkbookFolder.Trim().Trim('/');

    /// <summary>Workbook file name pattern ({facility}, {datim}, {date}).</summary>
    public string FileNameFormat => IsCare && !string.IsNullOrWhiteSpace(_reports.CareFileNameFormat)
        ? _reports.CareFileNameFormat
        : _reports.FileNameFormat;

    /// <summary>
    /// The file name, without extension, for this facility and date: FileNameFormat with {facility},
    /// {datim} and {date} (yyyy_MM_dd) filled in, e.g. Numan_General_Hospital_2026_11_05.
    /// </summary>
    public string BaseFileName(Facility facility, DateOnly date)
    {
        var format = string.IsNullOrWhiteSpace(FileNameFormat) ? "{facility}_{date}" : FileNameFormat;
        return format
            .Replace("{facility}", SanitizeName(facility.FacilityName), StringComparison.OrdinalIgnoreCase)
            .Replace("{datim}", SanitizeName(facility.DatimId), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", date.ToString("yyyy_MM_dd"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Logs the type and what it means, once at startup.</summary>
    public void Log(ILogger logger) =>
        logger.LogInformation(
            "[Facility] Type: {Type} (Facility:IsCommunityCentre = {Setting}) - workbooks go to '{Container}/{Folder}' named {Format}{Excluded}.",
            IsCare ? "community centre (care)" : "gen-pop", IsCare, UploadContainer, WorkbookFolder, FileNameFormat,
            ExcludedSheets.Count > 0 ? $", without {string.Join(" / ", ExcludedSheets)}" : ", all sheets");

    /// <summary>A name made safe for a file or blob name: spaces and characters Windows refuses become _.</summary>
    public static string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "UnknownFacility"
            : string.Concat(name.Trim().Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
