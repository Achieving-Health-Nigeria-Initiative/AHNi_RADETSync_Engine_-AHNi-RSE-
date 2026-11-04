using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Platform;

public class ReportCycleAudit(
    IOptions<ReportSettings> reportOptions,
    ILogger<ReportCycleAudit> logger)
{
    private readonly ReportSettings _settings = reportOptions.Value;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(ReportCycleAuditEntry entry, CancellationToken ct = default)
    {
        if (!_settings.EnableCycleAudit)
        {
            return;
        }

        try
        {
            var path = ResolveAuditPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                cycleId = entry.CycleId,
                trigger = entry.Trigger,
                runDateWat = entry.RunDateWat.ToString("yyyy-MM-dd"),
                cycleStartWat = entry.CycleStartWat,
                slot = entry.SlotLabel,
                facility = entry.FacilityName,
                datim = entry.DatimId,
                state = entry.State,
                authPassed = entry.AuthPassed,
                success = entry.Success,
                totalRows = entry.TotalRows,
                uploadUrl = entry.UploadUrl,
                error = entry.Error,
                durationMs = entry.DurationMs
            };

            var line = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;

            await _writeLock.WaitAsync(ct);
            try
            {
                await File.AppendAllTextAsync(path, line, ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Audit] Failed to write cycle audit record: {Message}", ex.Message);
        }
    }

    private string ResolveAuditPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CycleAuditPath))
        {
            return _settings.CycleAuditPath;
        }

        return Path.Combine(AppPaths.Logs, "cycle-audit.jsonl");
    }
}

/// <summary>One report cycle, as written to the cycle audit log.</summary>
public sealed record ReportCycleAuditEntry(
    string CycleId,
    string Trigger,
    DateOnly RunDateWat,
    DateTimeOffset CycleStartWat,
    string SlotLabel,
    string FacilityName,
    string DatimId,
    string State,
    bool AuthPassed,
    bool Success,
    int TotalRows,
    string UploadUrl,
    string Error,
    long DurationMs);
