using System.Data;
using AHNiRSE.Shared;
namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Abstraction over the PostgreSQL (lamisplus EMR) database connection.
///
/// Two overloads:
///   - ExecuteScriptAsync(sql)             → plain SQL, no parameters
///   - ExecuteScriptAsync(sql, parameters) → parameterised query, safe against SQL injection
///
/// Parameterised scripts use the named placeholders listed on <see cref="QueryParameters"/>
/// (:facility_id, :start_date, ...) or the LAMISPlus report module's positional ?1..?5 (quoted '?2' as in its
/// query.yml queries, or bare). PostgresService converts them to Npgsql-style $1..$5 before execution.
/// </summary>
public interface IDatabaseService
{
    /// <summary>Executes a plain SQL string (no user-supplied values).</summary>
    Task<DataTable> ExecuteScriptAsync(string sql, CancellationToken ct = default);

    /// <summary>
    /// Executes a parameterised query. All five <see cref="QueryParameters"/> values are bound;
    /// a script uses whichever placeholders it needs.
    /// </summary>
    Task<DataTable> ExecuteScriptAsync(string sql, QueryParameters parameters, CancellationToken ct = default);
    Facility GetFacilityInfo();

    /// <summary>
    /// The facility this LAMISPlus instance serves (base_organisation_unit.id), read the way the
    /// LAMISPlus report module does: base_application_user.current_organisation_unit_id of user 1.
    /// </summary>
    Task<long> GetFacilityIdAsync(CancellationToken ct = default);
}

/// <summary>
/// Strongly-typed parameter bag — prevents accidental argument swaps.
/// Add new parameter types here as new report scripts are introduced.
/// </summary>
public class QueryParameters
{
    /// <summary>:facility_id (?1, $1) — The facility's integer ID in base_organisation_unit.</summary>
    public required long FacilityId { get; init; }

    /// <summary>:start_date (?2, $2) — Start of the reporting period (inclusive).</summary>
    public required DateOnly StartDate { get; init; }

    /// <summary>:end_date (?3, $3) — End of the reporting period (inclusive). Also used for age calculation.</summary>
    public required DateOnly EndDate { get; init; }

    /// <summary>:previous_quarter_end (?4, $4) — End of the calendar quarter before EndDate's quarter.</summary>
    public DateOnly PreviousQuarterEnd => QuarterEndBefore(EndDate);

    /// <summary>:previous_previous_quarter_end (?5, $5) — End of the quarter before that.</summary>
    public DateOnly PreviousPreviousQuarterEnd => QuarterEndBefore(PreviousQuarterEnd);

    /// <summary>Placeholder names in binding order: :facility_id is $1 ... :previous_previous_quarter_end is $5.</summary>
    public static readonly string[] PlaceholderNames =
        ["facility_id", "start_date", "end_date", "previous_quarter_end", "previous_previous_quarter_end"];

    /// <summary>
    /// Last day of the quarter (Mar, Jun, Sep, Dec) before the one containing <paramref name="date"/>,
    /// as the report module's QuarterService.getPreviousQuarter computes it.
    /// </summary>
    private static DateOnly QuarterEndBefore(DateOnly date)
    {
        var quarterStart = new DateOnly(date.Year, (date.Month - 1) / 3 * 3 + 1, 1);
        return quarterStart.AddDays(-1);
    }
}
