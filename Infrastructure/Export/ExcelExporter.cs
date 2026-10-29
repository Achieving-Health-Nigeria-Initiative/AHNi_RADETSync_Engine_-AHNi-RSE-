using System.Data;
using System.Reflection;
using AHNiRSE.Core.Interfaces;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Infrastructure.Export;

public class ExcelExporter(ILogger<ExcelExporter> logger) : IExcelExporter
{
    private static readonly XLColor HeaderFg = XLColor.Black;
    private static readonly XLColor AltRowFill = XLColor.FromHtml("#F2F2F2");

    // ── Public: single-sheet (unchanged signature) ────────────────────────────
    public Task ExportAsync(
        IEnumerable<object> data,
        string outputPath,
        Dictionary<string, string> headerMap,
        CancellationToken ct = default)
        => ExportSingleSheetAsync(data, outputPath, "Report", headerMap, ct);

    // ── Public: multi-sheet ───────────────────────────────────────────────────
    /// <summary>
    /// Writes every sheet in <paramref name="sheets"/> into one .xlsx workbook.
    ///
    /// Sheet routing (per ExcelSheetDefinition):
    ///   • RawData != null  → WriteDataTableSheet  (column names become headers)
    ///   • Rows    != null  → WriteTypedSheet       (reflection + HeaderMap)
    ///   • both null        → writes "No data" placeholder
    /// </summary>
    public async Task ExportMultiSheetAsync(
        IReadOnlyList<ExcelSheetDefinition> sheets,
        string outputPath,
        CancellationToken ct = default)
    {
        EnsureDirectory(outputPath);

        using var wb = new XLWorkbook();

        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();

            var ws = wb.Worksheets.Add(SafeSheetName(sheet.SheetName));

            // ── Branch A: raw DataTable sheet ────────────────────────────────
            if (sheet.RawData is not null)
            {
                WriteDataTableSheet(ws, sheet.RawData);
                continue;
            }

            // ── Branch B: typed-object sheet ─────────────────────────────────
            var rows = sheet.Rows?.ToList() ?? [];

            if (rows.Count == 0)
            {
                if (sheet.HeaderMap is { Count: > 0 })
                {
                    WriteDictionaryHeaders(ws, sheet.HeaderMap);
                    ws.Cell(2, 1).Value = "No data";
                    FormatWorksheet(ws, 1);
                }
                else
                {
                    ws.Cell(1, 1).Value = "No data";
                }
                continue;
            }

            var props = GetReadableProperties(rows[0].GetType());
            if (props.Count == 0)
            {
                ws.Cell(1, 1).Value = "No data";
                continue;
            }

            WriteHeaders(ws, props, sheet.HeaderMap);
            WriteRows(ws, rows, props);
            FormatWorksheet(ws, rows.Count);
        }

        wb.SaveAs(outputPath);
        logger.LogInformation("[Excel] Saved multi-sheet workbook ({Sheets} sheet(s)): {Path}",
            sheets.Count, outputPath);

        await Task.CompletedTask;
    }

    // ── Private: write a DataTable directly (no model class required) ─────────
    /// <summary>
    /// Writes <paramref name="dt"/> to <paramref name="ws"/>.
    /// Row 1 = DataColumn.ColumnName values (bold, centred).
    /// Rows 2+ = data with alternating row shading.
    /// Auto-filter and column width are applied.
    /// </summary>
    private static void WriteDataTableSheet(IXLWorksheet ws, DataTable dt)
    {
        if (dt.Columns.Count == 0)
        {
            ws.Cell(1, 1).Value = "No data";
            return;
        }

        // ── Headers ───────────────────────────────────────────────────────────
        for (var col = 0; col < dt.Columns.Count; col++)
        {
            var cell = ws.Cell(1, col + 1);
            cell.Value = dt.Columns[col].ColumnName;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        // ── Data rows ─────────────────────────────────────────────────────────
        for (var rowIdx = 0; rowIdx < dt.Rows.Count; rowIdx++)
        {
            var dataRow = dt.Rows[rowIdx];

            for (var col = 0; col < dt.Columns.Count; col++)
            {
                var raw = dataRow[col];
                var cell = ws.Cell(rowIdx + 2, col + 1);

                cell.Value = raw switch
                {
                    null or DBNull => string.Empty,
                    DateTime dt2 => dt2,
                    bool b => b,
                    int i => i,
                    long l => l,
                    decimal dec => dec,
                    double dbl => dbl,
                    float fl => fl,
                    _ => raw.ToString() ?? string.Empty
                };
            }

            if (rowIdx % 2 == 1)
                ws.Row(rowIdx + 2).Style.Fill.BackgroundColor = AltRowFill;
        }

        if (dt.Rows.Count == 0)
            ws.Cell(2, 1).Value = "No data";

        // ── Format ────────────────────────────────────────────────────────────
        ws.RangeUsed()?.SetAutoFilter();
        foreach (var c in ws.ColumnsUsed())
            c.AdjustToContents(1, Math.Max(dt.Rows.Count + 1, 2), 10, 50);
    }

    // ── Private: single-sheet helper (unchanged) ──────────────────────────────
    private async Task ExportSingleSheetAsync(
        IEnumerable<object> data,
        string outputPath,
        string sheetName,
        Dictionary<string, string>? headerMap,
        CancellationToken ct)
    {
        EnsureDirectory(outputPath);

        var rows = data?.ToList() ?? [];
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SafeSheetName(sheetName));

        if (rows.Count == 0)
        {
            ws.Cell(1, 1).Value = "No data";
            wb.SaveAs(outputPath);
            logger.LogInformation("[Excel] Saved empty workbook: {Path}", outputPath);
            await Task.CompletedTask;
            return;
        }

        var props = GetReadableProperties(rows[0].GetType());
        if (props.Count == 0)
        {
            ws.Cell(1, 1).Value = "No data";
            wb.SaveAs(outputPath);
            await Task.CompletedTask;
            return;
        }

        WriteHeaders(ws, props, headerMap);
        WriteRows(ws, rows, props);
        FormatWorksheet(ws, rows.Count);

        wb.SaveAs(outputPath);
        logger.LogInformation("[Excel] Saved: {Path}", outputPath);
        await Task.CompletedTask;
    }

    // ── Typed-sheet helpers (unchanged) ───────────────────────────────────────
    private static List<PropertyInfo> GetReadableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
               .Where(p => p.CanRead)
               .ToList();

    private static void WriteHeaders(
        IXLWorksheet ws,
        IReadOnlyList<PropertyInfo> props,
        Dictionary<string, string>? headerMap)
    {
        for (var col = 0; col < props.Count; col++)
        {
            var prop = props[col];
            var header = headerMap != null && headerMap.TryGetValue(prop.Name, out var mapped)
                ? mapped : prop.Name;

            var cell = ws.Cell(1, col + 1);
            cell.Value = header;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void WriteDictionaryHeaders(
        IXLWorksheet ws,
        IReadOnlyDictionary<string, string> headerMap)
    {
        var col = 1;
        foreach (var entry in headerMap)
        {
            var cell = ws.Cell(1, col++);
            cell.Value = string.IsNullOrWhiteSpace(entry.Value) ? entry.Key : entry.Value;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
    }

    private static void WriteRows(
        IXLWorksheet ws,
        IReadOnlyList<object> rows,
        IReadOnlyList<PropertyInfo> props)
    {
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var item = rows[rowIndex];

            for (var col = 0; col < props.Count; col++)
            {
                var prop = props[col];
                var value = prop.GetValue(item);
                var cell = ws.Cell(rowIndex + 2, col + 1);

                cell.Value = value switch
                {
                    null => string.Empty,
                    DBNull => string.Empty,
                    DateTime dt => dt,
                    DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                    bool b => b,
                    int i => i,
                    long l => l,
                    decimal dec => dec,
                    double dbl => dbl,
                    float fl => fl,
                    _ => value.ToString() ?? string.Empty
                };

                if (prop.Name == "MonthsOfArvRefill" && value is decimal)
                    cell.Style.NumberFormat.Format = "0.0";
            }

            if (rowIndex % 2 == 1)
                ws.Row(rowIndex + 2).Style.Fill.BackgroundColor = AltRowFill;
        }
    }

    private static void FormatWorksheet(IXLWorksheet ws, int rowCount)
    {
        ws.RangeUsed()?.SetAutoFilter();
        foreach (var col in ws.ColumnsUsed())
            col.AdjustToContents(1, rowCount + 1, 10, 50);
    }

    private static void EnsureDirectory(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string SafeSheetName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '\\', '/', '?', '*', '[', ']' })
            .ToHashSet();

        var clean = string.Concat((name ?? "Sheet").Where(c => !invalid.Contains(c)));
        if (string.IsNullOrWhiteSpace(clean)) clean = "Sheet";
        return clean.Length > 31 ? clean[..31] : clean;
    }
}

