namespace AHNiRSE.Core.Interfaces;

public interface IHeartbeatService
{
    Task PingAsync(CancellationToken ct = default);

    /// <summary>
    /// Records a successful report upload: writes LastReportRun (the storage service's upload
    /// time), LastReportRows and LastReportUrl to the heartbeat table straight away. Never throws.
    /// </summary>
    Task RecordReportUploadAsync(
        string facilityName,
        string datimId,
        string state,
        DateTimeOffset uploadedAt,
        int rowCount,
        string uploadUrl,
        CancellationToken ct = default);

    void SetReportError(string errorMessage);
}
