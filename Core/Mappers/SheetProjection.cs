using System.Data;

namespace AHNiRSE.Application.Mappers;

/// <summary>
/// Reshapes a query result to a fixed list of sheet headers, so a report module query with its own column
/// names still produces the headers an existing sheet has always had.
/// </summary>
public static class SheetProjection
{
    /// <summary>
    /// Returns a table with exactly <paramref name="headers"/>, in order. Each header takes the source column
    /// named in <paramref name="sources"/> (null = no source, left empty), else the same-named column if there is
    /// one, else stays empty; headers in <paramref name="constants"/> get that value on every row. A source that
    /// already has exactly these headers (an earlier blob script) is returned unchanged.
    /// </summary>
    public static DataTable Project(
        DataTable source,
        IReadOnlyList<string> headers,
        IReadOnlyDictionary<string, string?>? sources = null,
        IReadOnlyDictionary<string, object?>? constants = null)
    {
        if (source.Columns.Count == headers.Count &&
            headers.Select((h, i) => source.Columns[i].ColumnName == h).All(same => same))
            return source;

        var table = new DataTable();
        var columnFor = new DataColumn?[headers.Count];

        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            if (constants?.ContainsKey(header) == true)
            {
                table.Columns.Add(header, typeof(string));
                continue;
            }

            var name = sources is not null && sources.TryGetValue(header, out var mapped) ? mapped : header;
            columnFor[i] = name is not null && source.Columns.Contains(name) ? source.Columns[name] : null;
            table.Columns.Add(header, columnFor[i]?.DataType ?? typeof(string));
        }

        foreach (DataRow row in source.Rows)
        {
            var values = new object?[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                values[i] = constants is not null && constants.TryGetValue(headers[i], out var constant)
                    ? constant
                    : columnFor[i] is { } column ? row[column] : null;
            }
            table.Rows.Add(values);
        }

        return table;
    }
}
