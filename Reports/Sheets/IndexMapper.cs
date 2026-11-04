using System.Data;
using System.Globalization;

namespace AHNiRSE.Reports;

/// <summary>
/// Shapes the Index sheet. The report module's HTS Index (family index / ICT) query
/// (report-script/index.sql, ReportRepository.getFamilyIndexReport) has its own column names; the sheet keeps
/// the 18 headers the earlier blob index_report.sql produced, in that order. Each header takes the column the
/// report module writes under the same header in its HTS_INDEX_REPORT; headers that report does not have are
/// left empty.
/// </summary>
public static class IndexMapper
{
    /// <summary>The Index sheet headers, in order.</summary>
    public static readonly string[] Headers =
    [
        "datimCode",
        "Index client code/unique ID",
        "Age",
        "Sex",
        "Index client entry point",
        "Date offered index testing",
        "Accepted Index Testing",
        "Date of Elicitation",
        "Index Contact ID",
        "Index Contact's Age",
        "Index Contact's Sex",
        "Relationship of contact to index",
        "Type of contact",
        "Counseling, Referral and Support services",
        "HIV Test Status",
        "HIV Test Result",
        "Date of HTS (dd-mm-yyyy)",
        "Date linked to Treatment & Care",
    ];

    private static readonly HashSet<string> IntHeaders = ["Age", "Index Contact's Age"];
    private static readonly HashSet<string> DateHeaders =
        ["Date offered index testing", "Date of Elicitation", "Date linked to Treatment & Care"];

    /// <summary>
    /// Builds the Index sheet table from the module query's rows. A table that already has the sheet
    /// headers (the blob script) is returned unchanged.
    /// </summary>
    public static DataTable Map(DataTable source)
    {
        if (source.Columns.Contains(Headers[0]))
            return source;

        var table = new DataTable();
        foreach (var header in Headers)
        {
            var type = IntHeaders.Contains(header) ? typeof(int)
                : DateHeaders.Contains(header) ? typeof(DateTime)
                : typeof(string);
            table.Columns.Add(header, type);
        }

        foreach (DataRow row in source.Rows)
        {
            var tested = Get(row, "datetestedhiv") as DateTime?;

            // Sources follow the report module's fillFamilyIndexMapper / FAMILY_INDEX_HEADER.
            table.Rows.Add(
                Get(row, "datimid"),             // Facility Id (Datim)
                Get(row, "clientcode"),          // Index client code/unique ID
                null,                            // index client's age: not in the HTS Index report
                null,                            // index client's sex: not in the HTS Index report
                Get(row, "entrypoint"),          // Index client entry point
                Get(row, "dateofservice"),       // Date offered index testing
                Get(row, "acceptedpns"),         // Accepted Index Testing
                Get(row, "dateofservice"),       // Date of Elicitation
                Get(row, "contactcode"),         // Index Contact ID
                Get(row, "age"),                 // Index Contact's Age
                Get(row, "sex"),                 // Index Contact's Sex
                Get(row, "relationshiptoindex"), // Relationship to Index
                null,                            // type of contact: not in the HTS Index report
                Get(row, "notificationmethod"),  // Notification Method
                null,                            // HIV test status: not in the HTS Index report
                Get(row, "hivtestresult"),       // HIV Test Result
                tested?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture), // Date of HTS (dd-mm-yyyy)
                Get(row, "dateenrolledart"));    // Date linked to Treatment & Care
        }

        return table;
    }

    private static object? Get(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] is not DBNull ? row[column] : null;
}
