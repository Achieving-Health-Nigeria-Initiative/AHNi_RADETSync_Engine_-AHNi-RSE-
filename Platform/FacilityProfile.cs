using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Platform;

/// <summary>
/// Whether this facility is a community centre ("care") or a gen-pop facility, which decides where its
/// workbooks go and what they hold: care → Storage:Azure:CareContainerName (community-center) named
/// Reports:CareFileNameFormat ({facility}_{datim}_{date}), without the Facility:CareExcludedSheets
/// (PMTCT HTS and Maternal Cohort); everything else → Storage:Azure:ContainerName (autoreport) named
/// Reports:FileNameFormat ({facility}_{date}), all sheets.
///
/// Decided once per service start, in this order (appsettings.json "Facility" section):
///   1. CheckBayCentral = false → IsCommunityCentre, nothing else.
///   2. BayCentral's facility roster (TypeEndpoint/&lt;DATIM&gt;, EMR Version Control). The answer is saved
///      to facility-profile.json next to the exe. If it differs from IsCommunityCentre, BayCentral wins
///      and a warning says to update the setting.
///   3. BayCentral can't be reached: the answer saved for this DATIM code at an earlier start.
///   4. Nothing saved: IsCommunityCentre (default false = gen-pop), and BayCentral is asked again at the
///      next cycle.
/// </summary>
public sealed class FacilityProfile(
    IDatabaseService database,
    IHttpClientFactory httpClientFactory,
    IOptions<ReportSettings> reportOptions,
    IOptions<StorageSettings> storageOptions,
    IOptions<FacilitySettings> facilityOptions,
    ILogger<FacilityProfile> logger)
{
    public const string HttpClientName = "InstallationAuthService"; // same Cloudflare-friendly client as verify-serial
    private const string Care = "care";

    private readonly ReportSettings _reports = reportOptions.Value;
    private readonly AzureStorageOptions _storage = storageOptions.Value.Azure;
    private readonly FacilitySettings _facility = facilityOptions.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _resolved;
    private volatile bool _isCare;

    public bool IsCare => _isCare;

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
            if (!_isCare)
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
    public string UploadContainer => _isCare && !string.IsNullOrWhiteSpace(_storage.CareContainerName)
        ? _storage.CareContainerName
        : _storage.ContainerName;

    /// <summary>Workbook file name pattern ({facility}, {datim}, {date}).</summary>
    public string FileNameFormat => _isCare && !string.IsNullOrWhiteSpace(_reports.CareFileNameFormat)
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

    private static string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "UnknownFacility"
            : string.Concat(name.Trim().Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    /// <summary>Works out the type on first use after a start; later calls return at once.</summary>
    public async Task EnsureResolvedAsync(CancellationToken ct = default)
    {
        if (_resolved)
            return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_resolved)
                return;

            var (isCare, definite) = await ResolveAsync(ct);
            _isCare = isCare;
            _resolved = definite;
            logger.LogInformation(
                "[Facility] Type: {Type} - workbooks go to '{Container}' named {Format}{Excluded}{Retry}.",
                isCare ? "care (community centre)" : "gen-pop", UploadContainer, FileNameFormat,
                ExcludedSheets.Count > 0 ? $", without {string.Join(" / ", ExcludedSheets)}" : "",
                definite ? "" : " (provisional: BayCentral will be asked again next cycle)");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(bool IsCare, bool Definite)> ResolveAsync(CancellationToken ct)
    {
        if (!_facility.CheckBayCentral)
        {
            logger.LogInformation(
                "[Facility] Facility:CheckBayCentral is false - using Facility:IsCommunityCentre = {Value} from appsettings.json.",
                _facility.IsCommunityCentre);
            return (_facility.IsCommunityCentre, true);
        }

        string datim;
        try
        {
            datim = database.GetFacilityInfo().DatimId?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Facility] Could not read the DATIM code from the database.");
            datim = string.Empty;
        }

        if (datim.Length == 0)
            return FromCache(null);

        var answer = await AskBayCentralAsync(datim, ct);
        if (answer is { } projectType)
        {
            Save(datim, projectType);
            var isCare = string.Equals(projectType, Care, StringComparison.OrdinalIgnoreCase);
            if (isCare != _facility.IsCommunityCentre)
            {
                logger.LogWarning(
                    "[Facility] BayCentral's roster says {Type} but Facility:IsCommunityCentre is {Setting} - using BayCentral's answer. " +
                    "Set IsCommunityCentre to {Suggested} in appsettings.json so runs without internet match.",
                    isCare ? "community centre (care)" : "gen-pop", _facility.IsCommunityCentre, isCare ? "true" : "false");
            }
            return (isCare, true);
        }

        return FromCache(datim);
    }

    /// <summary>The roster's project type ("" when not on the roster), or null when BayCentral gave no usable answer.</summary>
    private async Task<string?> AskBayCentralAsync(string datim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_facility.TypeEndpoint))
            return null;

        var url = $"{_facility.TypeEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(datim)}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            using var doc = TryParse(body);
            var root = doc?.RootElement;

            // A 404 from this endpoint carries the DATIM code: the facility is not on the roster.
            // Any other 404 (e.g. a BayCentral without the endpoint yet) is not an answer.
            if ((int)response.StatusCode == 404 && root?.ValueKind == JsonValueKind.Object && root.Value.TryGetProperty("datim", out _))
            {
                logger.LogWarning("[Facility] {Datim} is not on BayCentral's facility roster - treated as gen-pop.", datim);
                return string.Empty;
            }

            if (response.IsSuccessStatusCode && root?.ValueKind == JsonValueKind.Object &&
                root.Value.TryGetProperty("projectType", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? string.Empty;
            }

            logger.LogWarning("[Facility] Facility type lookup returned {Status} - using the saved answer if any.", (int)response.StatusCode);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Facility] Facility type lookup failed ({Message}) - using the saved answer if any.", ex.Message);
            return null;
        }
    }

    private (bool IsCare, bool Definite) FromCache(string? datim)
    {
        try
        {
            if (File.Exists(AppPaths.FacilityProfile))
            {
                var saved = JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(AppPaths.FacilityProfile));
                if (saved is not null && (datim is null || string.Equals(saved.Datim, datim, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation("[Facility] Using the saved facility type '{Type}' from {At:yyyy-MM-dd HH:mm}.",
                        string.IsNullOrEmpty(saved.ProjectType) ? "not on roster" : saved.ProjectType, saved.CheckedAt);
                    return (string.Equals(saved.ProjectType, Care, StringComparison.OrdinalIgnoreCase), true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Facility] Could not read {File}: {Message}", AppPaths.FacilityProfile, ex.Message);
        }

        logger.LogWarning(
            "[Facility] BayCentral not reachable and nothing saved - using Facility:IsCommunityCentre = {Value} for now.",
            _facility.IsCommunityCentre);
        return (_facility.IsCommunityCentre, false);
    }

    private void Save(string datim, string projectType)
    {
        try
        {
            var json = JsonSerializer.Serialize(new SavedProfile(datim, projectType, DateTimeOffset.UtcNow),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.FacilityProfile, json);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Facility] Could not save {File}: {Message}", AppPaths.FacilityProfile, ex.Message);
        }
    }

    private static JsonDocument? TryParse(string body)
    {
        try { return string.IsNullOrWhiteSpace(body) ? null : JsonDocument.Parse(body); }
        catch { return null; }
    }

    private sealed record SavedProfile(string Datim, string ProjectType, DateTimeOffset CheckedAt);
}
