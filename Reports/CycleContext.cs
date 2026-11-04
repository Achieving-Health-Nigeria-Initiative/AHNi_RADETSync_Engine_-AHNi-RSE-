namespace AHNiRSE.Reports;

/// <summary>
/// Immutable per-cycle context — computed ONCE by ReportOrchestrator at cycle start and passed
/// unchanged to RadetWorkbook, which must not compute its own dates.
///
/// FIELDS:
///
///   RunDate     — WAT date at cycle START. Used for upload folder and filename.
///                 e.g. "2026-04-30" → folder RADET/2026-04-30/, file _2026_04_30.xlsx
///                 Guaranteed to match even when a cycle crosses midnight because
///                 it is captured before any script executes.
///
///   WatToday    — Same as RunDate. Passed to ReportPeriods inside the workbook
///                 so the reporting period calculation also uses WAT, not UTC.
///                 Fixes Scenario D: at 23:30 WAT, DateTime.UtcNow is still yesterday.
///
///   CycleId     — 8-char hex for correlating log lines across the full pipeline.
///
///   TriggeredBy — "Startup" | "Cron:0 */3 * * *" | "Manual" | "ForceBackfill:&lt;job&gt;" — for diagnostics.
///
///   Backfill    — true for a force backfill: RunDate/WatToday are the requested as-of date, and the
///                 sheets ignore any StartDate/EndDate set in Reports:Scripts (defaults only).
/// </summary>
public sealed record CycleContext(
    DateOnly RunDate,
    DateOnly WatToday,
    string CycleId,
    string TriggeredBy = "Cron",
    bool Backfill = false);
