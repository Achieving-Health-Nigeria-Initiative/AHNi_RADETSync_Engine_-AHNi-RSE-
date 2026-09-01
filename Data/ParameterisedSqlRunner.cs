using System.Data;
using Npgsql;

namespace AHNiRSE.Data;

/// <summary>
/// Executes a plain SQL string (no parameter substitution needed —
/// all queries now return all-facilities data without :datim_code).
/// </summary>
public class ParameterisedSqlRunner
{
    public async Task<DataTable> RunAsync(
        NpgsqlConnection conn,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand(sql, conn)
        {
            CommandTimeout = commandTimeoutSeconds
        };

        var table = new DataTable();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        for (int i = 0; i < reader.FieldCount; i++)
            table.Columns.Add(reader.GetName(i), reader.GetFieldType(i));

        while (await reader.ReadAsync(ct))
        {
            var row = table.NewRow();
            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
            table.Rows.Add(row);
        }

        return table;
    }
}