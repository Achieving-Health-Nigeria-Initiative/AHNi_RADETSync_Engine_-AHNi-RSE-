using System.Data;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AHNiRSE.Infrastructure.Database;

public class PostgresService(
    IOptions<DatabaseSettings> options,
    ILogger<PostgresService> logger) : IDatabaseService
{
    private readonly DatabaseSettings _cfg = options.Value;

    // â”€â”€ PARAMETERLESS SQL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task<DataTable> ExecuteScriptAsync(
        string sql,
        CancellationToken ct = default)
    {
        ValidateSql(sql);

        return await ExecuteWithRetryAsync(
            operationName: "PlainSql",
            action: async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var cmd = new NpgsqlCommand(sql, conn)
                {
                    CommandTimeout = _cfg.CommandTimeoutSeconds
                };
                return await FillTableAsync(cmd, token);
            },
            ct);
    }

    // â”€â”€ PARAMETERIZED SQL â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    public async Task<DataTable> ExecuteScriptAsync(
        string sql,
        QueryParameters parameters,
        CancellationToken ct = default)
    {
        ValidateSql(sql);
        var npgsqlSql = ConvertParameters(sql);

        return await ExecuteWithRetryAsync(
            operationName: "ParameterizedSql",
            action: async token =>
            {
                await using var conn = await OpenConnectionAsync(token);
                await using var cmd = new NpgsqlCommand(npgsqlSql, conn)
                {
                    CommandTimeout = _cfg.CommandTimeoutSeconds
                };

                cmd.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Bigint,
                    Value = parameters.FacilityId
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Date,
                    Value = parameters.StartDate
                });
                cmd.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Date,
                    Value = parameters.EndDate
                });

                logger.LogDebug(
                    "[Database] Parameters bound: FacilityId={FacilityId}, " +
                    "StartDate={StartDate}, EndDate={EndDate}",
                    parameters.FacilityId,
                    parameters.StartDate,
                    parameters.EndDate);

                return await FillTableAsync(cmd, token);
            },
            ct);
    }

    // â”€â”€ FACILITY IDENTITY â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    /// <summary>
    /// Resolves the facility name, state, and DATIM ID from the LamISPlus EMR.
    /// Joins through patient_person â†’ organisation unit hierarchy so it works
    /// regardless of which user account the service runs under.
    /// Returns an empty <see cref="Facility"/> on any failure â€” callers must
    /// treat IsEmpty as a non-fatal condition and retry on the next cycle.
    /// </summary>
    public Facility GetFacilityInfo()
    {
        // Walk the org-unit tree: patient_person â†’ facility â†’ LGA â†’ State
        // and pull DATIM_ID from the identifier table in one read.
        const string query = @"
            SELECT
                facility_state.name AS state,
                facility.name       AS facility_name,
                datim.facility_code
            FROM patient_person p
            INNER JOIN base_organisation_unit facility
                    ON facility.id = p.facility_id
            INNER JOIN base_organisation_unit facility_lga
                    ON facility_lga.id = facility.parent_organisation_unit_id
            INNER JOIN base_organisation_unit facility_state
                    ON facility_state.id = facility_lga.parent_organisation_unit_id
            LEFT JOIN (
                SELECT
                    bou.id AS organisation_unit_id,
                    (
                        SELECT code
                        FROM   base_organisation_unit_identifier
                        WHERE  name                = 'DATIM_ID'
                          AND  organisation_unit_id = bou.id
                        LIMIT  1
                    ) AS facility_code
                FROM base_organisation_unit bou
            ) datim ON datim.organisation_unit_id = facility.id
            LIMIT 1;";

        try
        {
            if (string.IsNullOrWhiteSpace(_cfg.ConnectionString))
                throw new DatabaseException("Database:ConnectionString is not configured.");

            using var conn = new NpgsqlConnection(_cfg.ConnectionString);
            conn.Open();

            using var cmd = new NpgsqlCommand(query, conn)
            {
                CommandTimeout = _cfg.CommandTimeoutSeconds
            };
            using var reader = cmd.ExecuteReader();

            if (reader.Read())
            {
                var facilityName = SafeGetString(reader, "facility_name");
                var datimId = SafeGetString(reader, "facility_code");
                var state = SafeGetString(reader, "state");

                logger.LogInformation(
                    "[Database] Facility resolved â€” Name='{Name}', DatimId='{DatimId}', State='{State}'",
                    facilityName, datimId, state);

                return new Facility
                {
                    FacilityName = facilityName,
                    DatimId = datimId,
                    State = state
                };
            }

            logger.LogWarning(
                "[Database] GetFacilityInfo returned no rows. " +
                "Ensure patient_person contains at least one record with a valid facility_id.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Database] GetFacilityInfo failed: {Message}", ex.Message);
        }

        return new Facility(); // IsEmpty = true â€” caller handles gracefully
    }

    // â”€â”€ INTERNALS â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.ConnectionString))
            throw new DatabaseException("Database:ConnectionString is not configured.");

        var conn = new NpgsqlConnection(_cfg.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            logger.LogDebug(
                "[Database] Connected to {Database} on {Host}.",
                conn.Database, conn.Host);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task<DataTable> FillTableAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        logger.LogInformation(
            "[Database] Executing query (timeout: {TimeoutSeconds}s).",
            cmd.CommandTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var table = new DataTable();
        table.Load(reader);

        sw.Stop();
        logger.LogInformation(
            "[Database] Query completed. Rows={Rows}, DurationMs={DurationMs}.",
            table.Rows.Count, sw.ElapsedMilliseconds);

        return table;
    }

    private async Task<DataTable> ExecuteWithRetryAsync(
        string operationName,
        Func<CancellationToken, Task<DataTable>> action,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _cfg.MaxRetryAttempts);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(1, _cfg.RetryDelaySeconds));

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                logger.LogInformation(
                    "[Database] {Operation} attempt {Attempt}/{Max}.",
                    operationName, attempt, maxAttempts);

                return await action(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    "[Database] {Operation} timeout on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    operationName, attempt, maxAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                var delay = CalculateBackoffWithJitter(baseDelay, attempt);
                logger.LogWarning(
                    ex,
                    "[Database] Transient error in {Operation} on attempt {Attempt}/{Max}. Retrying in {Delay}.",
                    operationName, attempt, maxAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new DatabaseException(
                    $"Database operation '{operationName}' failed after {attempt} attempt(s): {ex.Message}",
                    ex);
            }
        }

        throw new DatabaseException(
            $"Database operation '{operationName}' failed after {_cfg.MaxRetryAttempts} attempt(s).");
    }

    // â”€â”€ STATIC HELPERS â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Returns empty string instead of throwing when a column is NULL or missing.
    /// </summary>
    private static string SafeGetString(NpgsqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is NpgsqlException { IsTransient: true })
            return true;

        if (ex is TimeoutException or IOException or SocketException)
            return true;

        return ex.InnerException is not null && IsTransient(ex.InnerException);
    }

    private static TimeSpan CalculateBackoffWithJitter(TimeSpan baseDelay, int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var delayMs = baseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var jitterMultiplier = 0.85 + (Random.Shared.NextDouble() * 0.3);
        var jittered = delayMs * jitterMultiplier;
        return TimeSpan.FromMilliseconds(Math.Min(jittered, 30_000));
    }

    private static string ConvertParameters(string sql)
        => Regex.Replace(sql, @"\?(\d+)", "$$$1");

    private static void ValidateSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new DatabaseException("SQL script must not be empty.");
    }
}
