namespace AHNiRSE.Data.Models;

public class RunAudit
{
    public long Id { get; set; }
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid CycleId { get; set; }
    public string DatimCode { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public DateOnly ReportDate { get; set; }
    public string Status { get; set; } = "PENDING";
    public int? RadetRows { get; set; }
    public int? HtsRows { get; set; }
    public int? IndexRows { get; set; }
    public int? PmtctHtsRows { get; set; }
    public int? MaternalRows { get; set; }
    public string? FileHash { get; set; }
    public string? BlobPath { get; set; }
    public string DbSource { get; set; } = "REPLICA";
    public int? ReplicaLagMs { get; set; }
    public int? DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
