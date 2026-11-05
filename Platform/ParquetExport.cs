using System.Collections;
using System.Data;
using System.Globalization;
using Parquet;
using Parquet.Schema;
using ParquetColumn = Parquet.Data.DataColumn;

namespace AHNiRSE.Platform;

/// <summary>
/// Writes a query result to a Parquet file exactly as the query returned it: same column names, same
/// order, no header mapping or value changes. Types follow the PostgreSQL column type
/// (see PostgresService.PgTypeKey) so the files read back typed (e.g. with pandas):
///
///   date                         → DATE
///   timestamp / timestamptz      → timestamp (timestamptz in UTC)
///   smallint / integer / bigint  → int16 / int32 / int64
///   numeric / real / double      → double
///   boolean                      → boolean
///   anything else (text, uuid, time, arrays, json...) → string
///
/// Every column is nullable. Extra file metadata (report, facility, period...) goes in the footer.
/// </summary>
public static class ParquetExport
{
    public static async Task WriteAsync(
        DataTable table,
        string path,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
    {
        // Values are converted first; the columns are then built on the schema's own fields, as Parquet.Net requires.
        var converted = table.Columns.Cast<DataColumn>().Select(c => ToParquet(c, table.Rows)).ToList();
        var schema = new ParquetSchema(converted.Select(c => (Field)c.Field).ToArray());
        var fields = schema.GetDataFields();

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, file, cancellationToken: ct);
        writer.CompressionMethod = CompressionMethod.Snappy;
        writer.CustomMetadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        if (table.Rows.Count == 0)
            return; // schema only

        using var rowGroup = writer.CreateRowGroup();
        for (var i = 0; i < fields.Length; i++)
            await rowGroup.WriteColumnAsync(new ParquetColumn(fields[i], converted[i].Values), ct);
    }

    /// <summary>The PostgreSQL type of each column, as recorded when the query ran.</summary>
    public static Dictionary<string, string> PgTypes(DataTable table) =>
        table.Columns.Cast<DataColumn>().ToDictionary(
            c => c.ColumnName,
            c => c.ExtendedProperties[PostgresService.PgTypeKey] as string ?? c.DataType.Name);

    private static (DataField Field, Array Values) ToParquet(DataColumn column, DataRowCollection rows)
    {
        var name = column.ColumnName;
        var pgType = (column.ExtendedProperties[PostgresService.PgTypeKey] as string ?? string.Empty).ToLowerInvariant();
        var type = column.DataType;

        if (type == typeof(DateTime) && pgType == "date")
            return Column(new DataField<DateOnly?>(name), rows, column, v => (DateOnly?)DateOnly.FromDateTime((DateTime)v));
        if (type == typeof(DateOnly))
            return Column(new DataField<DateOnly?>(name), rows, column, v => (DateOnly?)(DateOnly)v);
        if (type == typeof(DateTime))
            return Column(new DateTimeDataField(name, DateTimeFormat.DateAndTime, isNullable: true), rows, column, v => (DateTime?)(DateTime)v);
        if (type == typeof(DateTimeOffset))
            return Column(new DateTimeDataField(name, DateTimeFormat.DateAndTime, isNullable: true), rows, column, v => (DateTime?)((DateTimeOffset)v).UtcDateTime);
        if (type == typeof(bool))
            return Column(new DataField<bool?>(name), rows, column, v => (bool?)(bool)v);
        if (type == typeof(short))
            return Column(new DataField<short?>(name), rows, column, v => (short?)(short)v);
        if (type == typeof(int))
            return Column(new DataField<int?>(name), rows, column, v => (int?)(int)v);
        if (type == typeof(long))
            return Column(new DataField<long?>(name), rows, column, v => (long?)(long)v);
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            return Column(new DataField<double?>(name), rows, column, v => (double?)Convert.ToDouble(v, CultureInfo.InvariantCulture));

        return Column(new DataField<string>(name), rows, column, AsText);
    }

    private static (DataField Field, Array Values) Column<T>(DataField field, DataRowCollection rows, DataColumn column, Func<object, T> convert)
    {
        var values = new T[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var value = rows[i][column];
            values[i] = value is DBNull or null ? default! : convert(value);
        }
        return (field, values);
    }

    private static string AsText(object value) => value switch
    {
        string s => s,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        IEnumerable items and not string => string.Join(",", items.Cast<object?>().Select(o => o is null ? "" : AsText(o))),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
