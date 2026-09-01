namespace AHNiRSE.Configuration;

public class ReportSettings
{
    public string CronSchedule { get; set; } = "0 2 * * *";
    public string TimeZoneId { get; set; } = "W. Central Africa Standard Time";
    public bool ForceRerun { get; set; } = false;
    // MaxParallelFacilities kept for config-file backwards compatibility but no longer used
    public int MaxParallelFacilities { get; set; } = 1;
}