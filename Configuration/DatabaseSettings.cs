namespace AHNiRSE.Configuration;

public class DatabaseSettings
{
    public string PrimaryConnectionString { get; set; } = string.Empty;
    public string ReplicaConnectionString { get; set; } = string.Empty;
    public int CommandTimeoutSeconds { get; set; } = 300;
}
