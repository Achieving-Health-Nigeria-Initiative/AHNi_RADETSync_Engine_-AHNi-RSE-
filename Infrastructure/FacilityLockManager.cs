using AHNiRSE.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AHNiRSE.Infrastructure;

/// <summary>
/// Uses PostgreSQL advisory locks to prevent two RSE instances from processing
/// the same facility concurrently (e.g. during a manual rerun while the scheduler
/// is also running).
///
/// Lock key = lower 32 bits of the facility's internal DB id.
/// pg_try_advisory_lock is session-scoped — released automatically on disconnect.
/// </summary>
public class FacilityLockManager
{
    private readonly string _connectionString;

    public FacilityLockManager(IOptions<DatabaseSettings> settings)
    {
        // Advisory locks must be acquired on the PRIMARY — replica is read-only
        _connectionString = settings.Value.PrimaryConnectionString;
    }

    /// <summary>
    /// Tries to acquire an advisory lock for the given facility id.
    /// Returns a disposable handle that releases the lock on Dispose,
    /// or null if the lock is already held by another session.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(int facilityId, CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(:key)", conn);
        cmd.Parameters.AddWithValue("key", (long)facilityId);

        var acquired = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        if (!acquired)
        {
            await conn.DisposeAsync();
            return null;
        }

        return new LockHandle(conn, facilityId);
    }

    private sealed class LockHandle : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly int _facilityId;
        private bool _disposed;

        public LockHandle(NpgsqlConnection conn, int facilityId)
        {
            _conn       = conn;
            _facilityId = facilityId;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(:key)", _conn);
                cmd.Parameters.AddWithValue("key", (long)_facilityId);
                await cmd.ExecuteScalarAsync();
            }
            finally
            {
                await _conn.DisposeAsync();
            }
        }
    }
}
