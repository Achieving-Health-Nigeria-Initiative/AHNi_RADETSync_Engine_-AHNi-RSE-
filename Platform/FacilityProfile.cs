using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Platform;

/// <summary>
/// Whether this facility is a community centre ("care") or a gen-pop facility, which decides where its
/// workbooks go: care → Storage:Azure:CareContainerName (community-center) named Reports:CareFileNameFormat
/// ({facility}_{datim}_{date}); everything else → Storage:Azure:ContainerName (autoreport) named
/// Reports:FileNameFormat ({facility}_{date}).
///
/// Reports:FacilityType = care | genpop fixes it. With "auto" (the default) the type is asked of BayCentral
/// once per service start (Reports:FacilityTypeEndpoint/&lt;DATIM&gt;, from the EMR Version Control roster)
/// and kept until the service restarts. Each answer is saved to facility-profile.json next to the exe;
/// when BayCentral cannot be reached, the saved answer is used. With no answer and nothing saved, the
/// facility is treated as gen-pop for that cycle and BayCentral is asked again at the next one.
/// </summary>
public sealed class FacilityProfile(
    IDatabaseService database,
    IHttpClientFactory httpClientFactory,
    IOptions<ReportSettings> reportOptions,
    IOptions<StorageSettings> storageOptions,
    ILogger<FacilityProfile> logger)
{
    public const string HttpClientName = "InstallationAuthService"; // same Cloudflare-friendly client as verify-serial
    private const string Care = "care";

    private readonly ReportSettings _reports = reportOptions.Value;
    private readonly AzureStorageOptions _storage = storageOptions.Value.Azure;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _resolved;
    private volatile bool _isCare;

    public bool IsCare => _isCare;

    /// <summary>Container finished workbooks are uploaded to.</summary>
    public string UploadContainer => _isCare && !string.IsNullOrWhiteSpace(_storage.CareContainerName)
        ? _storage.CareContainerName
        : _storage.ContainerName;

    /// <summary>Workbook file name pattern ({facility}, {datim}, {date}).</summary>
    public string FileNameFormat => _isCare && !string.IsNullOrWhiteSpace(_reports.CareFileNameFormat)
        ? _reports.CareFileNameFormat
        : _reports.FileNameFormat;

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
                "[Facility] Type: {Type} - workbooks go to '{Container}' named {Format}{Retry}.",
                isCare ? "care (community centre)" : "gen-pop", UploadContainer, FileNameFormat,
                definite ? "" : " (provisional: BayCentral will be asked again next cycle)");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<(bool IsCare, bool Definite)> ResolveAsync(CancellationToken ct)
    {
        var configured = (_reports.FacilityType ?? "auto").Trim().ToLowerInvariant();
        if (configured is "care" or "genpop" or "ahnibridge")
        {
            logger.LogInformation("[Facility] Reports:FacilityType = {Type} (set in appsettings.json).", configured);
            return (configured == Care, true);
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
            return (string.Equals(projectType, Care, StringComparison.OrdinalIgnoreCase), true);
        }

        return FromCache(datim);
    }

    /// <summary>The roster's project type ("" when not on the roster), or null when BayCentral gave no usable answer.</summary>
    private async Task<string?> AskBayCentralAsync(string datim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_reports.FacilityTypeEndpoint))
            return null;

        var url = $"{_reports.FacilityTypeEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(datim)}";
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

        logger.LogWarning("[Facility] Facility type unknown and nothing saved - treating this facility as gen-pop for now.");
        return (false, false);
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
