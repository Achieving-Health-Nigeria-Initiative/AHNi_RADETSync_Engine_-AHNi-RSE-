namespace AHNiRSE.Core.Models;

/// <summary>
/// HIV Testing Services record — 26 columns returned by the LamISPlus HTS query.
/// Trimmed to match the exact SELECT output; unused fields have been removed.
/// </summary>
public class Hts
{
    // ── Identity / Location ───────────────────────────────────────────────────
    public string? DatimCode { get; set; }
    public string? PatientId { get; set; }
    public string? ClientCode { get; set; }
    public string? Sex { get; set; }
    public int? Age { get; set; }
    public string? MaritalStatus { get; set; }
    public string? LgaOfResidence { get; set; }
    public string? StateOfResidence { get; set; }

    // ── Visit ─────────────────────────────────────────────────────────────────
    public DateTime? DateVisit { get; set; }
    public string? FirstTimeVisit { get; set; }

    // ── Testing context ───────────────────────────────────────────────────────
    public string? EntryPoint { get; set; }
    public string? IndexClient { get; set; }
    public string? PreviouslyTested { get; set; }
    public string? TargetGroup { get; set; }
    public string? Source { get; set; }
    public string? TestingSetting { get; set; }
    public string? Modality { get; set; }
    public string? CounselingType { get; set; }
    public string? PregnancyStatus { get; set; }
    public string? IndexTesting { get; set; }

    // ── Previous test ─────────────────────────────────────────────────────────
    public DateTime? PreviousTestDate { get; set; }   // SQL alias: previousTestDate
    public string? PreviousTestResult { get; set; }
    public int? HtsCount { get; set; }

    // ── HIV result ────────────────────────────────────────────────────────────
    public string? FinalHivTestResult { get; set; }
    public DateTime? DateOfHivTesting { get; set; }

    // ── Prevention ────────────────────────────────────────────────────────────
    public string? PrepOffered { get; set; }
    public string? PrepAccepted { get; set; }

    // ── Geolocation ───────────────────────────────────────────────────────────
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
