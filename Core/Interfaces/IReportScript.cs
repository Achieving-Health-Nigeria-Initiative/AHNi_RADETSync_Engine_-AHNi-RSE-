namespace AHNiRSE.Core.Interfaces;

/// <summary>
/// Contract for all report types (RADET, HTS, …).
///
/// RunAsync receives a CycleContext that carries all date values computed ONCE
/// by ReportOrchestrator at cycle start. Scripts must not compute their own dates.
/// </summary>
public interface IReportScript
{
    /// <summary>Matches Reports:Scripts[].Name in appsettings.json.</summary>
    string Name { get; }

    /// <summary>
    /// Runs the full pipeline: read SQL → query DB → export Excel → upload blob.
    /// Returns (RowCount, UploadUrl, UploadedAt); UploadedAt is the storage service's time for the
    /// upload, or null when nothing was uploaded.
    /// Throw on unrecoverable errors — ScriptManager catches per-script and continues.
    /// </summary>
    Task<(int RowCount, string UploadUrl, DateTimeOffset? UploadedAt)> RunAsync(CycleContext context, CancellationToken ct = default);
}

/// <summary>
/// Immutable per-cycle context — computed ONCE by ReportOrchestrator, passed
/// unchanged through ScriptManager to every IReportScript.
///
/// FIELDS:
///
///   RunDate     — WAT date at cycle START. Used for upload folder and filename.
///                 e.g. "2026-04-30" → folder RADET/2026-04-30/, file _2026_04_30.xlsx
///                 Guaranteed to match even when a cycle crosses midnight because
///                 it is captured before any script executes.
///
///   WatToday    — Same as RunDate. Passed to ResolveDates() inside each script
///                 so the reporting period calculation also uses WAT, not UTC.
///                 Fixes Scenario D: at 23:30 WAT, DateTime.UtcNow is still yesterday.
///
///   CycleId     — 8-char hex for correlating log lines across the full pipeline.
///
///   TriggeredBy — "Startup" | "Cron:0 */3 * * *" | "Manual" | "ForceBackfill:&lt;job&gt;" — for diagnostics.
///
///   Backfill    — true for a force backfill: RunDate/WatToday are the requested as-of date, and the
///                 scripts ignore any StartDate/EndDate set in Reports:Scripts (defaults only).
/// </summary>
public sealed record CycleContext(
    DateOnly RunDate,
    DateOnly WatToday,
    string CycleId,
    string TriggeredBy = "Cron",
    bool Backfill = false);
