using AHNiRSE.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AHNiRSE.Infrastructure;

// Always returns DB-DR (read replica) for all read queries.
// No lag check, no fallback to primary, no pgbouncer involved.
// Primary is only used by AuditRepository and FacilityLockManager for writes.
public class DatabaseConnectionSelector
{
    private readonly DatabaseSettings _settings;

    public DatabaseConnectionSelector(IOptions<DatabaseSettings> settings)
    {
        _settings = settings.Value;
    }

    public (NpgsqlConnection connection, string source) GetConnection()
    {
        return (new NpgsqlConnection(_settings.ReplicaConnectionString), "REPLICA");
    }
}
