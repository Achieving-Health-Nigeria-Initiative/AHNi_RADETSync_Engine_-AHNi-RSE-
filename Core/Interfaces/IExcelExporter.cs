using System.Data;

namespace AHNiRSE.Core.Interfaces;

public interface IExcelExporter
{
    Task ExportAsync(
        IEnumerable<object> data,
        string outputPath,
        Dictionary<string, string> headerMap,
        CancellationToken ct = default);

    Task ExportMultiSheetAsync(
        IReadOnlyList<ExcelSheetDefinition> sheets,
        string outputPath,
        CancellationToken ct = default);
}

/// <summary>
/// Describes one worksheet inside a multi-sheet workbook.
///
/// Two modes â€” use ONE per sheet:
///
///   A) Typed rows (existing behaviour):
///        new ExcelSheetDefinition("RADET", radetRows.Cast{object}().ToList(), RadetHeaderMap)
///      Rows is enumerated via reflection; HeaderMap re-labels property names.
///
///   B) Raw DataTable (new â€” no typed model required):
///        new ExcelSheetDefinition("Index", RawData: indexDataTable)
///      Column names from the DataTable are written directly as headers.
///      HeaderMap is ignored when RawData is supplied.
///
/// Memory note: when RawData is supplied the caller owns the DataTable lifetime.
/// Dispose it AFTER ExportMultiSheetAsync returns, not before.
/// </summary>
public sealed record ExcelSheetDefinition(
    string SheetName,
    IEnumerable<object>? Rows = null,
    Dictionary<string, string>? HeaderMap = null,
    DataTable? RawData = null);
