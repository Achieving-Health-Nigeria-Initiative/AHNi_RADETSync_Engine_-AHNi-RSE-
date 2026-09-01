namespace AHNiRSE.Data.Models;

public class Facility
{
    public int Id { get; set; }
    public string DatimCode { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public string FacilityNameBlob { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Lga { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int ExpectedMinRows { get; set; } = 10;
}
