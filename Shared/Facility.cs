namespace AHNiRSE.Shared;

/// <summary>
/// Represents the facility resolved from the LamISPlus EMR database.
/// Used for naming output files, blobs, and heartbeat identity.
/// </summary>
public class Facility
{
    public string FacilityName { get; init; } = string.Empty;
    public string DatimId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;

    public bool IsEmpty => string.IsNullOrWhiteSpace(FacilityName);
}
