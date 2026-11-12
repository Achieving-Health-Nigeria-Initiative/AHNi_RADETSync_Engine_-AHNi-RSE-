namespace AHNiRSE.Core.Interfaces;

public interface IReportCycleAudit
{
    Task RecordAsync(ReportCycleAuditEntry entry, CancellationToken ct = default);
}

public sealed record ReportCycleAuditEntry(
    string CycleId,
    string Trigger,
    DateOnly RunDateWat,
    DateTimeOffset CycleStartWat,
    string SlotLabel,
    string FacilityName,
    string DatimId,
    string State,
    bool AuthPassed,
    bool Success,
    int TotalRows,
    string UploadUrl,
    string Error,
    long DurationMs);

