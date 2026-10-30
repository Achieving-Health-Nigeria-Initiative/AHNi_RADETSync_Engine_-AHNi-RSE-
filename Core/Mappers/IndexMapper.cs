using System.Data;
using System.Globalization;

namespace AHNiRSE.Application.Mappers;

/// <summary>
/// Shapes the Index sheet. The report module's index elicitation query (report-script/index.sql, query.yml
/// index-query) has its own column names; the sheet keeps the 18 headers the earlier blob index_report.sql
/// produced, in that order. Headers the module's query has no source for are left empty.
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

    /// <summary>The module query's "Accepted INS" column: a codeset id or Yes/No text.</summary>
    public const string AcceptedInsColumn = "Accepted INS";

    /// <summary>
    /// Builds the Index sheet table from the module query's rows. <paramref name="codesets"/> resolves
    /// numeric "Accepted INS" values (base_application_codeset id -> display); <paramref name="asOf"/> is the
    /// date contacts' ages are worked out at. A table that already has the sheet headers (the blob script)
    /// is returned unchanged.
    /// </summary>
    public static DataTable Map(DataTable source, IReadOnlyDictionary<long, string> codesets, DateOnly asOf)
    {
        if (source.Columns.Contains(Headers[0]))
            return source;

        var table = new DataTable();
        foreach (var header in Headers)
            table.Columns.Add(header, header is "Age" or "Index Contact's Age" ? typeof(int) : typeof(string));

        foreach (DataRow row in source.Rows)
        {
            var dob = Get(row, "Date of Birth") as DateTime?;
            var tested = Get(row, "Date Partner Came For Testing") as DateTime?;

            table.Rows.Add(
                Get(row, "Facility Id (Datim)"),
                Get(row, "Client Code"),
                DBNull.Value,                                  // index client's age: not in the module query
                DBNull.Value,                                  // index client's sex: not in the module query
                DBNull.Value,                                  // entry point: not in the module query
                DBNull.Value,                                  // date offered: not in the module query
                AcceptedIns(Get(row, AcceptedInsColumn) as string, codesets),
                DBNull.Value,                                  // date of elicitation: not in the module query
                DBNull.Value,                                  // contact ID: not in the module query
                dob is null ? DBNull.Value : AgeAt(DateOnly.FromDateTime(dob.Value), asOf),
                Get(row, "Sex"),                               // the contact's sex (hts_index_elicitation.sex)
                Get(row, "Relationship with index client"),
                DBNull.Value,                                  // type of contact: not in the module query
                Get(row, "Notification Method"),
                DBNull.Value,                                  // contact's HIV test status: not in the module query
                DBNull.Value,                                  // contact's HIV test result: not in the module query
                tested?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? (object)DBNull.Value,
                DBNull.Value);                                 // date linked: never filled
        }

        return table;
    }

    /// <summary>Numeric "Accepted INS" values that need a codeset lookup.</summary>
    public static IEnumerable<long> CodesetIds(DataTable source) =>
        source.Columns.Contains(AcceptedInsColumn)
            ? source.Rows.Cast<DataRow>()
                .Select(r => Get(r, AcceptedInsColumn) as string)
                .Where(v => long.TryParse(v, out _))
                .Select(long.Parse!)
                .Distinct()
            : [];

    /// <summary>Yes/No text as Yes/No, a codeset id as its display, anything else as stored.</summary>
    private static object AcceptedIns(string? value, IReadOnlyDictionary<long, string> codesets)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DBNull.Value;

        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                return "Yes";
            case "false" or "no" or "0":
                return "No";
        }

        return long.TryParse(value, out var id) && codesets.TryGetValue(id, out var display) ? display : value;
    }

    private static int AgeAt(DateOnly dob, DateOnly asOf)
    {
        var age = asOf.Year - dob.Year;
        return dob.AddYears(age) > asOf ? age - 1 : age;
    }

    private static object? Get(DataRow row, string column) =>
        row.Table.Columns.Contains(column) && row[column] is not DBNull ? row[column] : null;
}
