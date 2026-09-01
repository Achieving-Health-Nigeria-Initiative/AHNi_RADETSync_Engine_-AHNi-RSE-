using AHNiRSE.Data.Models;
using AHNiRSE.Infrastructure;
using Npgsql;

namespace AHNiRSE.Data;

public class FacilityRepository
{
    private readonly DatabaseConnectionSelector _db;

    public FacilityRepository(DatabaseConnectionSelector db)
    {
        _db = db;
    }

    public async Task<List<Facility>> GetActiveFacilitiesAsync(CancellationToken ct = default)
    {
        var (conn, _) = _db.GetConnection();
        await using var _ = conn;
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"SELECT id, datim_code, facility_name, facility_name_blob, state, lga, is_active, expected_min_rows
              FROM rse_facility_registry
              WHERE is_active = TRUE
              ORDER BY facility_name",
            conn);

        var facilities = new List<Facility>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            facilities.Add(new Facility
            {
                Id              = reader.GetInt32(0),
                DatimCode       = reader.GetString(1),
                FacilityName    = reader.GetString(2),
                FacilityNameBlob = reader.GetString(3),
                State           = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Lga             = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                IsActive        = reader.GetBoolean(6),
                ExpectedMinRows = reader.GetInt32(7)
            });
        }
        return facilities;
    }
}
