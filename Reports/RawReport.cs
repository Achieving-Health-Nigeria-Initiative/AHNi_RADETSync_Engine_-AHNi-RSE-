using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Reports;

/// <summary>What a raw report run uploaded.</summary>
public sealed record RawReportResult(
    int RadetRows,
    string ManifestUrl,
    DateTimeOffset UploadedAt,
    IReadOnlyList<string> Files);

/// <summary>
/// The raw report – only for force backfills requested as report type "raw". Runs the same seven
/// scripts as the RADET workbook (downloaded from blob storage, report-script/) with the same
/// parameters, and uploads each query result as it came from the database – no header mapping, no
/// value changes – as a Parquet file. A separate combiner turns them into the final workbooks.
///
///   &lt;container&gt;/raw-report/&lt;date&gt;/&lt;base&gt;_RADET.parquet
///                                     _HTS, _Index, _PMTCT_HTS, _Maternal_Cohort, _Prep, _AHD
///                                     _manifest.json   (uploaded last)
///
/// &lt;base&gt; is the workbook's file name for that date (Numan_General_Hospital_2026_11_05, or
/// Bauchi_Community_Center_xCya42gPOnU_2026_11_05 for a community centre) and the container is the
/// workbook's (autoreport or community-center).
///
/// All or nothing: if any enabled script fails, nothing is uploaded and the job fails with the reason.
/// A script switched off in Reports:Scripts (Enabled=false) is skipped and listed as such in the manifest.
/// The manifest goes up after every Parquet file, so a manifest means the set is complete.
/// </summary>
public sealed class RawReport(
    ReportScripts scripts,
    IStorageProvider storage,
    IDatabaseService database,
    FacilityProfile profile,
    ILogger<RawReport> logger)
{
    public const string Folder = "raw-report";

    /// <summary>
    /// The reports, in workbook sheet order: Reports:Scripts name, file suffix, the Reports:Scripts entry
    /// whose dates it uses (Index runs with RADET's period, as in the workbook) and its default start date.
    /// </summary>
    private static readonly (string Script, string Suffix, string PeriodScript, Func<DateOnly, DateOnly> DefaultStart)[] Reports =
    [
        (ReportScripts.Radet, "RADET",           ReportScripts.Radet, _ => ReportPeriods.CohortStart),
        ("HTS",               "HTS",             "HTS",               ReportPeriods.HtsStart),
        ("Index",             "Index",           ReportScripts.Radet, _ => ReportPeriods.CohortStart),
        ("PMTCTHTS",          "PMTCT_HTS",       "PMTCTHTS",          _ => ReportPeriods.CohortStart),
        ("Maternal",          "Maternal_Cohort", "Maternal",          _ => ReportPeriods.CohortStart),
        ("Prep",              "Prep",            "Prep",              _ => ReportPeriods.ReportModuleStart),
        ("AHD",               "AHD",             "AHD",               _ => ReportPeriods.ReportModuleStart),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record ReportFile(
        string Report, string Script, bool Enabled, string? LocalPath = null, string? File = null,
        int Rows = 0, int Columns = 0, DateOnly? StartDate = null, DateOnly? EndDate = null);

    public async Task<RawReportResult> RunAsync(CycleContext context, CancellationToken ct = default)
    {
        var facility = database.GetFacilityInfo();
        var radetCfg = scripts.Find(ReportScripts.Radet);
        var facilityId = radetCfg?.FacilityId > 0 ? radetCfg.FacilityId : await database.GetFacilityIdAsync(ct);
        var baseName = profile.BaseFileName(facility, context.RunDate);
        var folder = $"{Folder}/{context.RunDate:yyyy-MM-dd}";
        var tempDir = Path.Combine(Path.GetTempPath(), "AHNi-RSE", context.CycleId, "raw");

        logger.LogInformation(
            "[Raw] CycleId={Id} | AsOf={AsOf} | FacilityId={FacilityId} | -> {Container}/{Folder}/{Base}_*.parquet",
            context.CycleId, context.RunDate.ToString("yyyy-MM-dd"), facilityId, profile.UploadContainer, folder, baseName);

        try
        {
            // Same as the workbook: every query at once. The first failure stops the others.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = Reports
                .Select(r => RunOneAsync(r.Script, r.Suffix, r.PeriodScript, r.DefaultStart, facilityId, facility,
                    context, baseName, tempDir, cts))
                .ToList();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch when (!ct.IsCancellationRequested)
            {
                var failed = tasks
                    .Where(t => t.IsFaulted)
                    .Select(t => t.Exception!.InnerException!.Message)
                    .ToList();
                throw new InvalidOperationException(
                    $"Raw report not uploaded - {string.Join("; ", failed)}", tasks.First(t => t.IsFaulted).Exception!.InnerException);
            }

            var files = tasks.Select(t => t.Result).ToList();

            // Parquet files first, the manifest last.
            var uploaded = new List<string>();
            foreach (var file in files.Where(f => f.Enabled))
            {
                await storage.UploadAsync(file.LocalPath!, $"{folder}/{file.File}", ct);
                uploaded.Add($"{folder}/{file.File}");
            }

            var manifestName = $"{baseName}_manifest.json";
            var manifestPath = Path.Combine(tempDir, manifestName);
            await File.WriteAllTextAsync(manifestPath, Manifest(facility, context, folder, files), ct);
            var (manifestUrl, uploadedAt) = await storage.UploadAsync(manifestPath, $"{folder}/{manifestName}", ct);
            uploaded.Add($"{folder}/{manifestName}");

            var radetRows = files.First(f => f.Script == ReportScripts.Radet).Rows;
            logger.LogInformation(
                "[Raw] COMPLETE CycleId={Id} | {Summary} | {Folder}",
                context.CycleId,
                string.Join(" | ", files.Select(f => f.Enabled ? $"{f.Report}={f.Rows}" : $"{f.Report}=off")),
                folder);

            return new RawReportResult(radetRows, manifestUrl, uploadedAt, uploaded);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* non-fatal */ }
        }
    }

    private async Task<ReportFile> RunOneAsync(
        string script, string suffix, string periodScript, Func<DateOnly, DateOnly> defaultStart,
        long facilityId, Facility facility, CycleContext context, string baseName, string tempDir,
        CancellationTokenSource cts)
    {
        if (!scripts.IsEnabled(script))
        {
            logger.LogInformation("[Raw] {Report} is switched off in Reports:Scripts - skipped.", suffix);
            return new ReportFile(suffix, script, Enabled: false);
        }

        try
        {
            var (start, end) = ReportPeriods.Resolve(scripts.Find(periodScript), context, defaultStart);
            var parameters = new QueryParameters { FacilityId = facilityId, StartDate = start, EndDate = end };
            var sql = await scripts.LoadSqlAsync(script, cts.Token);

            using var data = await database.ExecuteScriptAsync(sql, parameters, cts.Token);
            var fileName = $"{baseName}_{suffix}.parquet";
            var localPath = Path.Combine(tempDir, fileName);

            await ParquetExport.WriteAsync(data, localPath, new Dictionary<string, string>
            {
                ["report"] = suffix,
                ["script"] = scripts.Find(script)?.BlobPath ?? string.Empty,
                ["facility"] = facility.FacilityName,
                ["datim_id"] = facility.DatimId,
                ["state"] = facility.State,
                ["as_of_date"] = context.RunDate.ToString("yyyy-MM-dd"),
                ["start_date"] = start.ToString("yyyy-MM-dd"),
                ["end_date"] = end.ToString("yyyy-MM-dd"),
                ["pg_types"] = JsonSerializer.Serialize(ParquetExport.PgTypes(data)),
            }, cts.Token);

            logger.LogInformation(
                "[Raw] {Report}: {Rows} row(s), {Cols} column(s), period {Start}->{End}.",
                suffix, data.Rows.Count, data.Columns.Count, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));

            return new ReportFile(suffix, script, true, localPath, fileName, data.Rows.Count, data.Columns.Count, start, end);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
        {
            cts.Cancel();
            logger.LogError(ex, "[Raw] {Report} failed: {Message}", suffix, ex.Message);
            throw new InvalidOperationException($"{suffix}: {ex.Message}", ex);
        }
    }

    private string Manifest(Facility facility, CycleContext context, string folder, List<ReportFile> files) =>
        JsonSerializer.Serialize(new
        {
            facility = facility.FacilityName,
            datimId = facility.DatimId,
            state = facility.State,
            facilityType = profile.IsCare ? "care" : "genpop",
            container = profile.UploadContainer,
            asOfDate = context.RunDate.ToString("yyyy-MM-dd"),
            job = context.TriggeredBy,
            cycleId = context.CycleId,
            generatedAtUtc = DateTimeOffset.UtcNow,
            rseVersion = typeof(RawReport).Assembly.GetName().Version?.ToString(),
            reports = files.Select(f => new
            {
                report = f.Report,
                script = scripts.Find(f.Script)?.BlobPath,
                enabled = f.Enabled,
                file = f.Enabled ? $"{folder}/{f.File}" : null,
                rows = f.Rows,
                columns = f.Columns,
                startDate = f.StartDate?.ToString("yyyy-MM-dd"),
                endDate = f.EndDate?.ToString("yyyy-MM-dd"),
            }),
        }, JsonOptions);
}
