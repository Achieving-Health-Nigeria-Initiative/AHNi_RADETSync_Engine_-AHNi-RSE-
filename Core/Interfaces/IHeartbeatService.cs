namespace AHNiRSE.Core.Interfaces;

public interface IHeartbeatService
{
    Task PingAsync(CancellationToken ct = default);

    // state parameter added — matches what GetFacilityInfo() now returns
    void SetReportSuccess(
        string facilityName,
        string datimId,
        string state,
        DateTimeOffset ranAt,
        int rowCount,
        string uploadUrl);

    void SetReportError(string errorMessage);
}
