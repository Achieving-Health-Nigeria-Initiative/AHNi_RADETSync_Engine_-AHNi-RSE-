using AHNiRSE.Configuration;
using AHNiRSE.Data;
using AHNiRSE.Export;
using AHNiRSE.Infrastructure;
using AHNiRSE.Storage;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Core;

public class CombinedReportJob
{
    private readonly DatabaseConnectionSelector _db;
    private readonly ParameterisedSqlRunner _sqlRunner;
    private readonly SqlScriptCache _scriptCache;
    private readonly FiveSheetExcelExporter _exporter;
    private readonly BlobUploader _uploader;
    private readonly DatabaseSettings _dbSettings;
    private readonly ILogger<CombinedReportJob> _logger;

    public CombinedReportJob(
        DatabaseConnectionSelector db,
        ParameterisedSqlRunner sqlRunner,
        SqlScriptCache scriptCache,
        FiveSheetExcelExporter exporter,
        BlobUploader uploader,
        IOptions<DatabaseSettings> dbSettings,
        ILogger<CombinedReportJob> logger)
    {
        _db = db;
        _sqlRunner = sqlRunner;
        _scriptCache = scriptCache;
        _exporter = exporter;
        _uploader = uploader;
        _dbSettings = dbSettings.Value;
        _logger = logger;
    }

    public async Task RunAsync(Guid cycleId, DateOnly reportDate, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var (conn, _) = _db.GetConnection();
            await using var __ = conn;
            await conn.OpenAsync(ct);

            var radetSql    = await _scriptCache.GetRadetSqlAsync(ct);
            var htsSql      = await _scriptCache.GetHtsSqlAsync(ct);
            var indexSql    = await _scriptCache.GetIndexSqlAsync(ct);
            var pmtctHtsSql = await _scriptCache.GetPmtctHtsSqlAsync(ct);
            var maternalSql = await _scriptCache.GetMaternalSqlAsync(ct);

            int timeout = _dbSettings.CommandTimeoutSeconds;

            _logger.LogInformation("Running RADET query — all facilities");
            var radet = await _sqlRunner.RunAsync(conn, radetSql, timeout, ct);

            _logger.LogInformation("Running HTS query — all facilities");
            var hts = await _sqlRunner.RunAsync(conn, htsSql, timeout, ct);

            _logger.LogInformation("Running INDEX query — all facilities");
            var index = await _sqlRunner.RunAsync(conn, indexSql, timeout, ct);

            _logger.LogInformation("Running PMTCT-HTS query — all facilities");
            var pmtctHts = await _sqlRunner.RunAsync(conn, pmtctHtsSql, timeout, ct);

            _logger.LogInformation("Running MATERNAL query — all facilities");
            var maternal = await _sqlRunner.RunAsync(conn, maternalSql, timeout, ct);

            _logger.LogInformation(
                "Queries complete — RADET:{R} HTS:{H} INDEX:{I} PMTCT:{P} MAT:{M}",
                radet.Rows.Count, hts.Rows.Count, index.Rows.Count, pmtctHts.Rows.Count, maternal.Rows.Count);

            if (radet.Rows.Count == 0)
                throw new InvalidOperationException("RADET returned 0 rows — aborting upload");

            var filename = FacilityFilenameBuilder.BuildCombined(reportDate);
            var tempPath = Path.Combine(Path.GetTempPath(), filename);

            try
            {
                _exporter.Export(radet, hts, index, pmtctHts, maternal, tempPath);

                var blobPath = await _uploader.UploadCombinedReportAsync(tempPath, filename, reportDate, ct);

                sw.Stop();
                RseMetrics.FacilitiesProcessed.Inc();
                RseMetrics.FacilityDurationSeconds.Observe(sw.Elapsed.TotalSeconds);

                _logger.LogInformation(
                    "SUCCESS — RADET:{R} HTS:{H} INDEX:{I} PMTCT:{P} MAT:{M} — {Ms}ms — {Blob}",
                    radet.Rows.Count, hts.Rows.Count, index.Rows.Count, pmtctHts.Rows.Count, maternal.Rows.Count,
                    sw.ElapsedMilliseconds, blobPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            RseMetrics.FacilitiesFailed.Inc();
            _logger.LogError(ex, "FAILED after {Ms}ms", sw.ElapsedMilliseconds);
        }
    }
}
