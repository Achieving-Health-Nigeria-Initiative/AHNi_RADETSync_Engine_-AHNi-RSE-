using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Licensing;

/// <summary>
/// Records failed licence checks in their own Azure table (Auth:FailureTableName, default RseAuthFailures), apart
/// from the heartbeat, so the Facility Health Monitor works as before. One row per facility PC: PartitionKey = the
/// DATIM ID the PC sent, RowKey = its BIOS serial - the pair BayCentral's Serial Numbers list is checked against.
///
///   Status          failing while the check fails; resolved once BayCentral accepts the same PC again
///   Reason          refused (serial / DATIM ID not registered), serial-unavailable (BIOS serial could not be
///                   read), inconclusive (BayCentral not reachable and Auth:PermitWhenInconclusive=false)
///   FirstFailedAt / LastFailedAt / FailureCount   since the row last became failing
///   ResolvedAt      when it last passed after failing
///   FacilityName, State, MachineName, AppVersion, Package (live / test)
///
/// Writing here never affects the check or the cycle: a failed write is only logged. Auth:FailureTableName = ""
/// turns it off.
/// </summary>
public sealed class AuthFailureLog
{
    public const string Failing = "failing";
    public const string Resolved = "resolved";

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(15);

    private readonly TableClient? _table;
    private readonly ILogger<AuthFailureLog> _logger;
    private readonly string _appVersion;
    private readonly string _package;
    private bool _tableReady;

    public AuthFailureLog(
        TableServiceClient tableService,
        FacilityProfile profile,
        IOptions<AuthSettings> options,
        ILogger<AuthFailureLog> logger)
    {
        _logger = logger;
        var tableName = options.Value.FailureTableName?.Trim();
        _table = string.IsNullOrEmpty(tableName) ? null : tableService.GetTableClient(tableName);
        _appVersion = typeof(AuthFailureLog).Assembly.GetName().Version?.ToString() ?? "unknown";
        _package = profile.WorkbookFolder.Equals("RADET", StringComparison.OrdinalIgnoreCase) ? "live" : "test";
    }

    /// <summary>Adds or updates this PC's row as failing.</summary>
    public async Task RecordFailureAsync(Facility facility, string serial, string reason)
    {
        if (_table is null) return;
        try
        {
            using var cts = new CancellationTokenSource(WriteTimeout);
            await EnsureTableAsync(cts.Token);

            var (partitionKey, rowKey) = Keys(facility.DatimId, serial);
            var now = DateTimeOffset.UtcNow;
            var existing = await _table.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey, cancellationToken: cts.Token);
            var previous = existing.HasValue ? existing.Value : null;
            var stillFailing = previous?.GetString("Status") == Failing;

            var entity = new TableEntity(partitionKey, rowKey)
            {
                ["Datim"] = facility.DatimId,
                ["DeviceSerial"] = serial,
                ["FacilityName"] = facility.FacilityName,
                ["State"] = facility.State,
                ["MachineName"] = Environment.MachineName,
                ["AppVersion"] = _appVersion,
                ["Package"] = _package,
                ["Status"] = Failing,
                ["Reason"] = reason,
                ["FirstFailedAt"] = stillFailing ? previous!.GetDateTimeOffset("FirstFailedAt") ?? now : now,
                ["LastFailedAt"] = now,
                ["FailureCount"] = stillFailing ? (previous!.GetInt32("FailureCount") ?? 0) + 1 : 1,
            };
            if (previous?.GetDateTimeOffset("ResolvedAt") is { } resolvedAt)
                entity["ResolvedAt"] = resolvedAt; // when it last recovered, kept for history

            await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cts.Token);
            _logger.LogInformation("[Auth] Failure recorded in {Table}: Facility={Facility}, DATIM={Datim}, Device={Device}, Reason={Reason}.",
                _table.Name, facility.FacilityName, facility.DatimId, serial, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Auth] Could not record the failure in {Table}: {Message}", _table.Name, Describe(ex));
        }
    }

    /// <summary>Marks this PC's row resolved if it is failing; does nothing if it has no row.</summary>
    public async Task RecordPassAsync(Facility facility, string serial)
    {
        if (_table is null) return;
        try
        {
            using var cts = new CancellationTokenSource(WriteTimeout);
            await EnsureTableAsync(cts.Token);

            var (partitionKey, rowKey) = Keys(facility.DatimId, serial);
            var existing = await _table.GetEntityIfExistsAsync<TableEntity>(partitionKey, rowKey,
                select: ["Status"], cancellationToken: cts.Token);
            if (!existing.HasValue || existing.Value!.GetString("Status") != Failing)
                return;

            var update = new TableEntity(partitionKey, rowKey)
            {
                ["Status"] = Resolved,
                ["ResolvedAt"] = DateTimeOffset.UtcNow,
                ["AppVersion"] = _appVersion,
            };
            if (!string.IsNullOrWhiteSpace(facility.FacilityName))
                update["FacilityName"] = facility.FacilityName; // keep the stored name if the EMR could not be read
            await _table.UpdateEntityAsync(update, ETag.All, TableUpdateMode.Merge, cts.Token);
            _logger.LogInformation("[Auth] {Table}: Facility={Facility}, DATIM={Datim}, Device={Device} marked resolved.",
                _table.Name, facility.FacilityName, facility.DatimId, serial);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Auth] Could not mark the failure resolved in {Table}: {Message}", _table.Name, Describe(ex));
        }
    }

    /// <summary>One line for the log: Azure's own messages run to several lines of response headers.</summary>
    private static string Describe(Exception ex) => ex is RequestFailedException rf
        ? $"{rf.Status} {rf.ErrorCode}"
        : ex.Message;

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableReady) return;
        await _table!.CreateIfNotExistsAsync(ct);
        _tableReady = true;
    }

    /// <summary>Table keys may not contain / \ # ? or control characters; those become _.</summary>
    internal static (string PartitionKey, string RowKey) Keys(string datim, string serial) =>
        (Clean(datim), Clean(serial));

    private static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DeviceIdentity.UnknownIdentity;
        var chars = value.Trim().Select(c => c is '/' or '\\' or '#' or '?' || char.IsControl(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
