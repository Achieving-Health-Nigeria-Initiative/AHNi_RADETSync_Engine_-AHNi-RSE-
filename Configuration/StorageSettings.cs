namespace AHNiRSE.Configuration;

public class StorageSettings
{
    public string AzureBlobConnectionString { get; set; } = string.Empty;
    public string ReportContainer { get; set; } = "autoreport";
    public string ScriptContainer { get; set; } = "radet-sql";
    public string ScriptPrefix { get; set; } = "scripts/";
    public string ReportPrefix { get; set; } = "RADET";
}
