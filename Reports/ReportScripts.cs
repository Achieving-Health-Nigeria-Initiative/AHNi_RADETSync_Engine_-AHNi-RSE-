using Microsoft.Extensions.Options;

namespace AHNiRSE.Reports;

/// <summary>
/// The SQL behind each sheet. Every script is downloaded at the start of each cycle from the
/// Storage:Azure:ScriptContainerName container (autoreport, folder report-script/), so updating a
/// blob updates every facility on its next run. A Reports:Scripts entry can point a sheet at another
/// blob (BlobPath), switch it off (Enabled=false) or fix its dates.
/// </summary>
public sealed class ReportScripts(IStorageProvider storage, IOptions<ReportSettings> options)
{
    public const string Radet = "RADET";

    /// <summary>Default blob of each Reports:Scripts entry, used when the entry has no BlobPath.</summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultBlobs =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Radet] = "report-script/radet.sql",
            ["HTS"] = "report-script/hts.sql",
            ["Index"] = "report-script/index.sql",
            ["PMTCTHTS"] = "report-script/pmtct_hts.sql",
            ["Maternal"] = "report-script/maternal.sql",
            ["Prep"] = "report-script/prep.sql",
            ["AHD"] = "report-script/ahd.sql",
        };

    public ScriptConfig? Find(string name) =>
        options.Value.Scripts.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A sheet runs unless its Reports:Scripts entry exists and says Enabled=false.</summary>
    public bool IsEnabled(string name) => Find(name)?.Enabled ?? true;

    /// <summary>Downloads the entry's BlobPath (or the sheet's default blob) and returns the SQL.</summary>
    public async Task<string> LoadSqlAsync(string name, CancellationToken ct)
    {
        var blobPath = Find(name)?.BlobPath is { Length: > 0 } configured ? configured : DefaultBlobs[name];
        var sqlPath = await storage.DownloadAsync(blobPath, ct);
        if (!File.Exists(sqlPath))
            throw new FileNotFoundException($"{name} SQL missing: {blobPath}");

        return await File.ReadAllTextAsync(sqlPath, ct);
    }
}
