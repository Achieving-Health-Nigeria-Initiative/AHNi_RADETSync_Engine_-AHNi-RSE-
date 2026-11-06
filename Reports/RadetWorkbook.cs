using System.Data;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Reports;

/// <summary>
/// The RADET workbook – seven sheets, built from the LAMISPlus report module queries and uploaded to
/// RADET/&lt;date&gt;/&lt;facility&gt;_&lt;date&gt;.xlsx.
///
/// Sheet 1 – RADET          (radet.sql     – typed model, strongly mapped)
/// Sheet 2 – HTS            (hts.sql       – typed model, strongly mapped)
/// Sheet 3 – Index          (index.sql     – report module HTS Index (ICT), mapped to the sheet's 18 headers)
/// Sheet 4 – PMTCT HTS      (pmtct_hts.sql – report module PMTCT HTS (Pepfar), mapped to the sheet's 16 headers)
/// Sheet 5 – Maternal       (maternal.sql  – report module query.yml maternal cohort, mapped to 48 headers)
/// Sheet 6 – Prep           (prep.sql      – report module query.yml prep-query, headers as returned)
/// Sheet 7 – AHD            (ahd.sql       – report module query.yml ahd-query, headers as returned)
///
/// Sheet 1 is FATAL: if the RADET SQL cannot be downloaded or run, nothing is uploaded.
/// Sheets 2-7 are NON-FATAL: if a SQL download or query fails, or the sheet's Reports:Scripts entry
/// has Enabled=false, the sheet keeps its header row with no data rows and the upload still
/// completes. Sheets 2-7 run in parallel with RADET.
///
/// Dates come from CycleContext only (see ReportPeriods): the file name and folder use RunDate, so
/// they match even when a cycle crosses midnight.
///
/// Memory: RADET and HTS DataTables are disposed as soon as they are mapped to models; the other
/// sheets' DataTables are held until the workbook is written, then disposed. The temp .xlsx is
/// deleted after upload.
/// </summary>
public sealed class RadetWorkbook(
    ReportScripts scripts,
    IStorageProvider storage,
    IDatabaseService database,
    ExcelExporter exporter,
    FacilityProfile profile,
    ILogger<RadetWorkbook> logger)
{
    /// <summary>
    /// Builds and uploads the workbook. Returns the RADET row count, the upload URL and Azure's
    /// upload time. Throws when the RADET sheet fails or the upload fails.
    /// </summary>
    public async Task<(int RowCount, string UploadUrl, DateTimeOffset UploadedAt)> RunAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        var cfg = scripts.Find(ReportScripts.Radet)
            ?? throw new InvalidOperationException($"No '{ReportScripts.Radet}' config in Reports:Scripts.");

        var (startDate, endDate) = ReportPeriods.Resolve(cfg, context, ReportPeriods.CohortStart);
        var facilityId = cfg.FacilityId > 0 ? cfg.FacilityId : await database.GetFacilityIdAsync(ct);
        var queryParameters = new QueryParameters
        {
            FacilityId = facilityId,
            StartDate = startDate,
            EndDate = endDate
        };

        logger.LogInformation(
            "[RADET] CycleId={Id} | RunDate={RunDate} | WatToday={WatToday} | FacilityId={FacilityId} | " +
            "Period={Start}->{End} | PreviousQuarterEnd={Previous} | PreviousPreviousQuarterEnd={PreviousPrevious}",
            context.CycleId,
            context.RunDate.ToString("yyyy-MM-dd"),
            context.WatToday.ToString("yyyy-MM-dd"),
            facilityId,
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            queryParameters.PreviousQuarterEnd.ToString("yyyy-MM-dd"),
            queryParameters.PreviousPreviousQuarterEnd.ToString("yyyy-MM-dd"));

        var facility = database.GetFacilityInfo();
        var fileName = profile.BaseFileName(facility, context.RunDate) + ".xlsx";
        var localOutput = Path.Combine(Path.GetTempPath(), "AHNi-RSE", context.CycleId, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(localOutput)!);

        using var parallelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Community centres leave some sheets out (Facility:CareExcludedSheets): not queried, not in the workbook.
        var excluded = profile.ExcludedSheets;
        if (excluded.Count > 0)
            logger.LogInformation("[RADET] Community centre - left out: {Sheets} (Facility:CareExcludedSheets).",
                string.Join(", ", SheetScripts.Where(kv => excluded.Contains(kv.Value)).Select(kv => kv.Key)));
        static Task<(DataTable? Data, int Count)> Skipped() => Task.FromResult<(DataTable?, int)>((null, 0));

        var htsTask = excluded.Contains("HTS")
            ? Task.FromResult<(List<Hts> Rows, int Count)>(([], 0))
            : RunHtsSheetAsync(facilityId, context, parallelCts.Token);
        // Index runs with the RADET period.
        var indexTask = excluded.Contains("Index")
            ? Skipped()
            : RunShapedSheetAsync("Index", "Index", queryParameters, IndexMapper.Map, parallelCts.Token);

        // Default start dates are the ones the earlier blob scripts used (the report module's own default is 1985).
        var datim = new Dictionary<string, object?> { [SheetLayouts.DatimCode] = facility.DatimId };
        var pmtctTask = excluded.Contains("PMTCTHTS") ? Skipped() : RunShapedSheetAsync(
            "PMTCTHTS", "PMTCT HTS",
            SheetParameters("PMTCTHTS", facilityId, context, ReportPeriods.CohortStart),
            rows => SheetProjection.Project(rows, SheetLayouts.PmtctHtsHeaders, SheetLayouts.PmtctHtsSources, datim),
            parallelCts.Token);
        var maternalTask = excluded.Contains("Maternal") ? Skipped() : RunShapedSheetAsync(
            "Maternal", "Maternal Cohort",
            SheetParameters("Maternal", facilityId, context, ReportPeriods.CohortStart),
            rows => SheetProjection.Project(rows, SheetLayouts.MaternalCohortHeaders, SheetLayouts.MaternalCohortSources, datim),
            parallelCts.Token);
        // Prep and AHD: the report module queries return exactly the sheets' headers.
        var prepTask = excluded.Contains("Prep") ? Skipped() : RunDataTableSheetAsync(
            "Prep", "Prep", SheetParameters("Prep", facilityId, context, ReportPeriods.ReportModuleStart), parallelCts.Token);
        var ahdTask = excluded.Contains("AHD") ? Skipped() : RunDataTableSheetAsync(
            "AHD", "AHD", SheetParameters("AHD", facilityId, context, ReportPeriods.ReportModuleStart), parallelCts.Token);

        List<Radet> radetRows;
        int radetCount;
        try
        {
            var radetSql = await scripts.LoadSqlAsync(ReportScripts.Radet, ct);
            using var radetData = await database.ExecuteScriptAsync(radetSql, queryParameters, ct);
            radetRows = RadetMapper.Map(radetData);
            radetCount = radetData.Rows.Count;
        }
        catch
        {
            parallelCts.Cancel();
            await AwaitSafelyAsync(htsTask, indexTask, pmtctTask, maternalTask, prepTask, ahdTask);
            throw;
        }

        logger.LogInformation("[RADET] RADET query returned {Rows} row(s).", radetCount);
        if (radetCount == 0)
        {
            logger.LogWarning(
                "[RADET] Zero rows - FacilityId={Fid}, Period={Start}->{End}. Verify facility has active patients for this period.",
                facilityId, startDate, endDate);
        }

        var htsResult = await htsTask;
        var indexResult = await indexTask;
        var pmtctResult = await pmtctTask;
        var maternalResult = await maternalTask;
        var prepResult = await prepTask;
        var ahdResult = await ahdTask;

        var sheets = new List<ExcelSheetDefinition>
        {
            new(SheetName: "RADET",           Rows: radetRows.Cast<object>().ToList(),      HeaderMap: SheetHeaders.Radet),
            new(SheetName: "HTS",             Rows: htsResult.Rows.Cast<object>().ToList(), HeaderMap: SheetHeaders.Hts),
            new(SheetName: "Index",           RawData: indexResult.Data,    HeaderMap: SheetHeaders.Index),
            new(SheetName: "PMTCT HTS",       RawData: pmtctResult.Data,    HeaderMap: SheetHeaders.PmtctHts),
            new(SheetName: "Maternal Cohort", RawData: maternalResult.Data, HeaderMap: SheetHeaders.MaternalCohort),
            new(SheetName: "Prep",            RawData: prepResult.Data,     HeaderMap: SheetHeaders.Prep),
            new(SheetName: "AHD",             RawData: ahdResult.Data),
        };
        sheets.RemoveAll(sheet => SheetScripts.TryGetValue(sheet.SheetName, out var script) && excluded.Contains(script));

        try
        {
            await exporter.ExportMultiSheetAsync(sheets, localOutput, ct);
            var blobDest = $"RADET/{context.RunDate:yyyy-MM-dd}/{fileName}";
            var (storagePath, uploadedAt) = await storage.UploadAsync(localOutput, blobDest, ct);
            TryDeleteTempFile(localOutput);

            logger.LogInformation(
                "[RADET] COMPLETE CycleId={Id} | RADET={R} | HTS={H} | Index={I} | PMTCTHTS={P} | Maternal={M} | Prep={Pr} | AHD={Ahd} rows | {Dest}",
                context.CycleId, radetCount, htsResult.Count, indexResult.Count, pmtctResult.Count,
                maternalResult.Count, prepResult.Count, ahdResult.Count, blobDest);

            return (radetCount, storagePath, uploadedAt);
        }
        finally
        {
            indexResult.Data?.Dispose();
            pmtctResult.Data?.Dispose();
            maternalResult.Data?.Dispose();
            prepResult.Data?.Dispose();
            ahdResult.Data?.Dispose();
        }
    }

    // ── Non-fatal sheets ──────────────────────────────────────────────────────

    private async Task<(List<Hts> Rows, int Count)> RunHtsSheetAsync(long facilityId, CycleContext context, CancellationToken ct)
    {
        if (!scripts.IsEnabled("HTS"))
        {
            logger.LogInformation("[RADET] HTS sheet disabled in Reports:Scripts - Sheet 2 will be empty.");
            return ([], 0);
        }

        try
        {
            var parameters = SheetParameters("HTS", facilityId, context, ReportPeriods.HtsStart);
            var sql = await scripts.LoadSqlAsync("HTS", ct);
            using var data = await database.ExecuteScriptAsync(sql, parameters, ct);
            var count = data.Rows.Count;
            var rows = HtsMapper.Map(data);
            logger.LogInformation("[RADET] HTS query returned {Rows} row(s).", count);
            return (rows, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] HTS query failed - Sheet 2 will be empty.");
            return ([], 0);
        }
    }

    /// <summary>
    /// A sheet whose report module query has its own column names: runs it, then <paramref name="shape"/>
    /// turns the rows into the sheet's headers.
    /// </summary>
    private async Task<(DataTable? Data, int Count)> RunShapedSheetAsync(
        string scriptName,
        string sheetName,
        QueryParameters parameters,
        Func<DataTable, DataTable> shape,
        CancellationToken ct)
    {
        var (data, count) = await RunDataTableSheetAsync(scriptName, sheetName, parameters, ct);
        if (data is null)
            return (null, 0);

        try
        {
            var mapped = shape(data);
            if (!ReferenceEquals(mapped, data))
                data.Dispose();
            return (mapped, count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] {Sheet} rows could not be mapped - sheet will be empty.", sheetName);
            data.Dispose();
            return (null, 0);
        }
    }

    private async Task<(DataTable? Data, int Count)> RunDataTableSheetAsync(
        string scriptName,
        string sheetName,
        QueryParameters parameters,
        CancellationToken ct)
    {
        if (!scripts.IsEnabled(scriptName))
        {
            logger.LogInformation("[RADET] {Sheet} sheet disabled in Reports:Scripts - sheet will be empty.", sheetName);
            return (null, 0);
        }

        try
        {
            var sql = await scripts.LoadSqlAsync(scriptName, ct);
            var data = await database.ExecuteScriptAsync(sql, parameters, ct);
            var count = data.Rows.Count;
            logger.LogInformation("[RADET] {Sheet} query returned {Rows} row(s).", sheetName, count);
            return (data, count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RADET] {Sheet} query failed - sheet will be empty.", sheetName);
            return (null, 0);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The facility and the sheet's period (see ReportPeriods).</summary>
    private QueryParameters SheetParameters(
        string scriptName, long facilityId, CycleContext context, DateOnly defaultStart)
        => SheetParameters(scriptName, facilityId, context, _ => defaultStart);

    private QueryParameters SheetParameters(
        string scriptName, long facilityId, CycleContext context, Func<DateOnly, DateOnly> defaultStart)
    {
        var (start, end) = ReportPeriods.Resolve(scripts.Find(scriptName), context, defaultStart);
        logger.LogInformation(
            "[RADET] {Script} period {Start}->{End}", scriptName, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
        return new QueryParameters { FacilityId = facilityId, StartDate = start, EndDate = end };
    }

    private static async Task AwaitSafelyAsync(params Task[] tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch { /* best effort: the RADET failure is what gets reported */ }
    }

    /// <summary>Workbook sheet title → its Reports:Scripts name (RADET is never left out).</summary>
    private static readonly Dictionary<string, string> SheetScripts = new()
    {
        ["HTS"] = "HTS",
        ["Index"] = "Index",
        ["PMTCT HTS"] = "PMTCTHTS",
        ["Maternal Cohort"] = "Maternal",
        ["Prep"] = "Prep",
        ["AHD"] = "AHD",
    };

    private static void TryDeleteTempFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* non-fatal */ }
    }
}
