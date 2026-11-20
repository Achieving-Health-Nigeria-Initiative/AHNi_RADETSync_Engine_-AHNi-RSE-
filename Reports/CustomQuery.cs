using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Reports;

/// <summary>
/// What a force backfill job of report type "custom" asks for, from the job row the Backfill app wrote.
/// </summary>
/// <param name="ScriptName">The script's name in the Backfill app, e.g. vl_eligibility.</param>
/// <param name="ScriptBlob">custom-script/&lt;name&gt;/&lt;sha256&gt;.sql in the script container (autoreport).</param>
/// <param name="ScriptSha256">SHA-256 of that blob when the run was requested; a different blob is refused.</param>
/// <param name="StartDate">:start_date / ?2.</param>
/// <param name="EndDate">:end_date / ?3 (the quarter ends ?4 and ?5 come from it).</param>
/// <param name="Parameters">Values of the script's own :name parameters.</param>
/// <param name="RequestId">The Backfill app's request, which names the results folder.</param>
public sealed record CustomJob(
    string ScriptName,
    string ScriptBlob,
    string ScriptSha256,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyDictionary<string, string> Parameters,
    string RequestId)
{
    public const string BlobFolder = "custom-script";
}

/// <summary>
/// Runs a custom SQL script (force backfill report type "custom") and uploads its result as one Parquet file:
///
///   &lt;CustomScript:Container&gt;/&lt;script&gt;/&lt;request id&gt;/&lt;Facility&gt;_&lt;DATIM&gt;.parquet   (custom-reports by default)
///
/// The script comes from the job's ScriptBlob and must match the SHA-256 recorded on the job. It must pass
/// SqlGuard (one SELECT/WITH query, nothing that writes) and then runs in a READ ONLY transaction that is
/// rolled back, within CustomScript:TimeoutSeconds and CustomScript:MaxRows. The Parquet footer records the
/// script, its SHA-256, the facility and every parameter value, so a file explains itself.
/// </summary>
public sealed class CustomQuery(
    IStorageProvider storage,
    IDatabaseService database,
    ReportScripts scripts,
    IOptions<CustomScriptSettings> options,
    ILogger<CustomQuery> logger)
{
    private readonly CustomScriptSettings _cfg = options.Value;

    /// <summary>The limit for the whole job: the query, plus time to fetch the script and upload the result.</summary>
    public int JobTimeoutSeconds => Math.Max(10, _cfg.TimeoutSeconds) + 300;

    public async Task<BackfillResult> RunAsync(CustomJob job, CycleContext context, CancellationToken ct = default)
    {
        if (!_cfg.Enabled)
            throw new InvalidOperationException("Custom scripts are switched off on this RSE (CustomScript:Enabled=false).");

        var facility = database.GetFacilityInfo();
        if (facility.IsEmpty)
            throw new InvalidOperationException("The facility could not be read from the LAMISPlus database.");
        var radetCfg = scripts.Find(ReportScripts.Radet);
        var facilityId = radetCfg?.FacilityId > 0 ? radetCfg.FacilityId : await database.GetFacilityIdAsync(ct);

        logger.LogInformation(
            "[Custom] CycleId={Id} | Script={Script} ({Sha}) | Request={Request} | {Start} -> {End} | FacilityId={FacilityId}",
            context.CycleId, job.ScriptName, Short(job.ScriptSha256), job.RequestId,
            job.StartDate.ToString("yyyy-MM-dd"), job.EndDate.ToString("yyyy-MM-dd"), facilityId);

        // 1. The script, exactly as it was when the run was requested.
        var sql = await LoadScriptAsync(job, ct);

        // 2. Checked, and its parameters turned into $1, $2 ...
        var analysis = SqlGuard.Analyze(sql);
        if (!analysis.IsValid)
            throw new InvalidOperationException($"Script refused: {string.Join(" ", analysis.Errors)}");
        var missing = analysis.CustomParameters.Where(p => !job.Parameters.ContainsKey(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"No value for parameter(s) {string.Join(", ", missing.Select(p => ":" + p))} - set them when requesting the run.");
        var extraValues = analysis.CustomParameters.Select(p => job.Parameters[p]).ToList();

        // 3. Read-only run.
        var parameters = new QueryParameters { FacilityId = facilityId, StartDate = job.StartDate, EndDate = job.EndDate };
        using var data = await database.ExecuteReadOnlyAsync(
            analysis.ToPostgres(), parameters, extraValues, _cfg.TimeoutSeconds, _cfg.MaxRows, ct);

        // 4. Parquet, uploaded to the custom container.
        var fileName = $"{FacilityProfile.SanitizeName(facility.FacilityName)}_{FacilityProfile.SanitizeName(facility.DatimId)}.parquet";
        var blobName = $"{Segment(job.ScriptName)}/{Segment(job.RequestId)}/{fileName}";
        var tempDir = Path.Combine(Path.GetTempPath(), "AHNi-RSE", context.CycleId, "custom");
        var localPath = Path.Combine(tempDir, fileName);

        try
        {
            await ParquetExport.WriteAsync(data, localPath, new Dictionary<string, string>
            {
                ["report"] = "custom",
                ["script"] = job.ScriptName,
                ["script_blob"] = job.ScriptBlob,
                ["script_sha256"] = job.ScriptSha256,
                ["request_id"] = job.RequestId,
                ["facility"] = facility.FacilityName,
                ["datim_id"] = facility.DatimId,
                ["state"] = facility.State,
                ["facility_id"] = facilityId.ToString(),
                ["start_date"] = job.StartDate.ToString("yyyy-MM-dd"),
                ["end_date"] = job.EndDate.ToString("yyyy-MM-dd"),
                ["parameters"] = JsonSerializer.Serialize(
                    analysis.CustomParameters.ToDictionary(p => p, p => job.Parameters[p])),
                ["rows"] = data.Rows.Count.ToString(),
                ["pg_types"] = JsonSerializer.Serialize(ParquetExport.PgTypes(data)),
                ["rse_version"] = typeof(CustomQuery).Assembly.GetName().Version?.ToString() ?? string.Empty,
                ["generated_at_utc"] = DateTimeOffset.UtcNow.ToString("o"),
            }, ct);

            var (url, uploadedAt) = await storage.UploadToAsync(_cfg.Container, localPath, blobName, ct);
            logger.LogInformation(
                "[Custom] COMPLETE CycleId={Id} | {Rows} row(s), {Cols} column(s) -> {Container}/{Blob}",
                context.CycleId, data.Rows.Count, data.Columns.Count, _cfg.Container, blobName);
            return new BackfillResult(data.Rows.Count, url, uploadedAt, [blobName], _cfg.Container);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* non-fatal */ }
        }
    }

    private async Task<string> LoadScriptAsync(CustomJob job, CancellationToken ct)
    {
        // Only scripts the Backfill app stored: never another blob in the script container.
        if (!job.ScriptBlob.StartsWith(CustomJob.BlobFolder + "/", StringComparison.Ordinal)
            || !job.ScriptBlob.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            || job.ScriptBlob.Contains(".."))
            throw new InvalidOperationException($"'{job.ScriptBlob}' is not a custom script ({CustomJob.BlobFolder}/...sql).");

        var path = await storage.DownloadAsync(job.ScriptBlob, ct);
        var bytes = await File.ReadAllBytesAsync(path, ct);
        try { File.Delete(path); } catch { /* staged copy; non-fatal */ }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!sha.Equals(job.ScriptSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The script's SHA-256 ({Short(sha)}) is not the one recorded on the job ({Short(job.ScriptSha256)}) - " +
                "it changed after the run was requested, so it was not run.");

        return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
    }

    // A script name or request id as one blob path segment.
    private static string Segment(string value) =>
        string.Concat(value.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] : sha;
}
