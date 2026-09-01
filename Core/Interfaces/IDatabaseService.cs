using System.Data;
using AHNiRSE.Shared;
namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Abstraction over the PostgreSQL (lamisplus EMR) database connection.
///
/// Two overloads:
///   - ExecuteScriptAsync(sql)             â†’ plain SQL, no parameters
///   - ExecuteScriptAsync(sql, parameters) â†’ parameterised query, safe against SQL injection
///
/// The RADET query uses JPA-style positional markers (?1, ?2, ?3).
/// PostgresService converts these to Npgsql-style ($1, $2, $3) before execution.
/// </summary>
public interface IDatabaseService
{
    /// <summary>Executes a plain SQL string (no user-supplied values).</summary>
    Task<DataTable> ExecuteScriptAsync(string sql, CancellationToken ct = default);

    /// <summary>
    /// Executes a parameterised query.
    /// Parameters are passed in positional order matching ?1, ?2, ?3 â€¦ in the SQL file.
    /// </summary>
    Task<DataTable> ExecuteScriptAsync(string sql, QueryParameters parameters, CancellationToken ct = default);
    Facility GetFacilityInfo();
}

/// <summary>
/// Strongly-typed parameter bag â€” prevents accidental argument swaps.
/// Add new parameter types here as new report scripts are introduced.
/// </summary>
public class QueryParameters
{
    /// <summary>?1 â€” The facility's integer ID in base_organisation_unit.</summary>
    public required long FacilityId { get; init; }

    /// <summary>?2 â€” Start of the reporting period (inclusive).</summary>
    public required DateOnly StartDate { get; init; }

    /// <summary>?3 â€” End of the reporting period (inclusive). Also used for age calculation.</summary>
    public required DateOnly EndDate { get; init; }
}
