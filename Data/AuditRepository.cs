using AHNiRSE.Configuration;
using AHNiRSE.Data.Models;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AHNiRSE.Data;

public class AuditRepository
{
    private readonly DatabaseSettings _settings;

    public AuditRepository(IOptions<DatabaseSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task InsertAsync(RunAudit audit, CancellationToken ct = default)
    {
        // Always write audit to primary — never to replica
        await using var conn = new NpgsqlConnection(_settings.PrimaryConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO rse_run_audit
                (run_id, cycle_id, datim_code, facility_name, report_date, status,
                 radet_rows, hts_rows, index_rows, pmtcthts_rows, maternal_rows,
                 file_hash, blob_path, db_source, replica_lag_ms, duration_ms,
                 error_message, started_at, completed_at)
              VALUES
                (@runId, @cycleId, @datimCode, @facilityName, @reportDate, @status,
                 @radetRows, @htsRows, @indexRows, @pmtctHtsRows, @maternalRows,
                 @fileHash, @blobPath, @dbSource, @replicaLagMs, @durationMs,
                 @errorMessage, @startedAt, @completedAt)",
            conn);

        cmd.Parameters.AddWithValue("runId", audit.RunId);
        cmd.Parameters.AddWithValue("cycleId", audit.CycleId);
        cmd.Parameters.AddWithValue("datimCode", audit.DatimCode);
        cmd.Parameters.AddWithValue("facilityName", audit.FacilityName);
        cmd.Parameters.AddWithValue("reportDate", audit.ReportDate);
        cmd.Parameters.AddWithValue("status", audit.Status);
        cmd.Parameters.AddWithValue("radetRows", (object?)audit.RadetRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("htsRows", (object?)audit.HtsRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("indexRows", (object?)audit.IndexRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pmtctHtsRows", (object?)audit.PmtctHtsRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("maternalRows", (object?)audit.MaternalRows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("fileHash", (object?)audit.FileHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("blobPath", (object?)audit.BlobPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("dbSource", audit.DbSource);
        cmd.Parameters.AddWithValue("replicaLagMs", (object?)audit.ReplicaLagMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("durationMs", (object?)audit.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("errorMessage", (object?)audit.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("startedAt", (object?)audit.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("completedAt", (object?)audit.CompletedAt ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Checks whether the combined all-facilities run already succeeded for this report date.
    /// datim_code = 'COMBINED' is the sentinel used by CombinedReportJob.
    /// </summary>
    public async Task<bool> CombinedAlreadySucceededAsync(DateOnly reportDate, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_settings.PrimaryConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            @"SELECT COUNT(*) FROM rse_run_audit
              WHERE datim_code  = 'COMBINED'
                AND report_date = @reportDate
                AND status      = 'SUCCESS'",
            conn);

        cmd.Parameters.AddWithValue("reportDate", reportDate);

        var count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return count > 0;
    }
}