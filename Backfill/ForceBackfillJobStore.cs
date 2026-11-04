using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Backfill;

/// <summary>A force backfill job claimed by this machine.</summary>
public sealed record ForceBackfillJob(string DatimId, string RowKey, string RequestId, DateOnly AsOfDate, int Attempts);

/// <summary>
/// Reads and updates this facility's rows in the ForceBackfillJobs table, which BayCentral fills
/// when a superadmin requests a RADET backfill (one row per facility per as-of date).
///
/// TABLE LAYOUT (shared with BayCentral):
///   PartitionKey = DatimId, RowKey = "{RequestId}_{yyyy-MM-dd}"
///   Status: pending → running → uploaded | failed   (BayCentral may also set cancelled, or reset to pending)
///
/// CLAIMING: a pending job is set to running with an ETag-conditional update. If BayCentral cancelled
/// it, or another machine with the same DatimId claimed it first, Azure answers 412 and the job is skipped.
/// </summary>
public sealed class ForceBackfillJobStore
{
    private readonly TableClient _table;
    private readonly ForceBackfillSettings _cfg;
    private readonly ILogger<ForceBackfillJobStore> _logger;
    private readonly string _machineName = Environment.MachineName;
    private readonly string _appVersion;
    private readonly string _containerName;

    public ForceBackfillJobStore(
        TableServiceClient tableService,
        IOptions<ForceBackfillSettings> options,
        ILogger<ForceBackfillJobStore> logger,
        IOptions<StorageSettings>? storage = null)
    {
        _cfg = options.Value;
        _logger = logger;
        _containerName = storage?.Value.Azure.ContainerName is { Length: > 0 } c ? c : "autoreport";
        _table = tableService.GetTableClient(_cfg.TableName);
        _appVersion = typeof(ForceBackfillJobStore).Assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default)
        => await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Jobs this machine left running when the service stopped go back to pending, or to failed
    /// once they have used all MaxAttempts. Called when the backfill loop starts.
    /// </summary>
    public Task ReleaseInterruptedAsync(string datimId, CancellationToken ct = default)
        => ReleaseAsync(
            TableClient.CreateQueryFilter(
                $"PartitionKey eq {datimId} and Status eq {"running"} and MachineName eq {_machineName}"),
            (job, attempts) => $"Interrupted {attempts} time(s) - the service stopped during the run",
            ct);

    /// <summary>
    /// Jobs running for longer than <paramref name="staleAfter"/> since they were picked up, on any
    /// machine. No run lasts that long, so the machine that took the job stopped and never came back
    /// (retired after a database migration, renamed, or dead) and nothing else would ever release it.
    /// They go back to pending, or to failed once they have used all MaxAttempts.
    /// </summary>
    public Task ReleaseAbandonedAsync(string datimId, TimeSpan staleAfter, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - staleAfter;
        return ReleaseAsync(
            TableClient.CreateQueryFilter(
                $"PartitionKey eq {datimId} and Status eq {"running"} and PickedUpAt lt {cutoff}"),
            (job, attempts) =>
                $"Abandoned by {job.GetString("MachineName") ?? "another machine"}: no result " +
                $"{staleAfter.TotalHours:0.#}h after it was picked up ({attempts} attempt(s))",
            ct);
    }

    private async Task ReleaseAsync(string filter, Func<TableEntity, int, string> failReason, CancellationToken ct)
    {
        await foreach (var job in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            var attempts = job.GetInt32("Attempts") ?? 0;
            var giveUp = attempts >= _cfg.MaxAttempts;
            var patch = new TableEntity(job.PartitionKey, job.RowKey)
            {
                ["Status"] = giveUp ? "failed" : "pending",
            };
            if (giveUp)
            {
                patch["Error"] = failReason(job, attempts);
                patch["CompletedAt"] = DateTimeOffset.UtcNow;
            }

            try
            {
                // Conditional: if the job finished or BayCentral changed it meanwhile, leave it.
                await _table.UpdateEntityAsync(patch, job.ETag, TableUpdateMode.Merge, ct);
                _logger.LogWarning(
                    "[Backfill] Job {Job} released from {Machine} (picked up {PickedUp:o}, attempt {A}/{Max}) - now {Status}.",
                    job.RowKey, job.GetString("MachineName"), job.GetDateTimeOffset("PickedUpAt"),
                    attempts, _cfg.MaxAttempts, patch["Status"]);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
            }
        }
    }

    /// <summary>
    /// Claims this facility's oldest pending job (RowKeys start with the time-sortable RequestId),
    /// or returns null when there is none.
    /// </summary>
    public async Task<ForceBackfillJob?> TryClaimNextAsync(string datimId, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {datimId} and Status eq {"pending"}");
        var pending = new List<TableEntity>();
        await foreach (var job in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
            pending.Add(job);

        foreach (var job in pending.OrderBy(j => j.RowKey, StringComparer.Ordinal))
        {
            var attempts = job.GetInt32("Attempts") ?? 0;
            if (attempts >= _cfg.MaxAttempts)
                continue;

            if (!DateOnly.TryParseExact(job.GetString("AsOfDate"), "yyyy-MM-dd", out var asOfDate))
            {
                await TryUpdateAsync(job.PartitionKey, job.RowKey, new()
                {
                    ["Status"] = "failed",
                    ["Error"] = $"Invalid AsOfDate '{job.GetString("AsOfDate")}'",
                    ["CompletedAt"] = DateTimeOffset.UtcNow,
                }, ct);
                continue;
            }

            var claim = new TableEntity(job.PartitionKey, job.RowKey)
            {
                ["Status"] = "running",
                ["PickedUpAt"] = DateTimeOffset.UtcNow,
                ["Attempts"] = attempts + 1,
                ["MachineName"] = _machineName,
                ["AppVersion"] = _appVersion,
                ["Error"] = string.Empty,
            };

            try
            {
                await _table.UpdateEntityAsync(claim, job.ETag, TableUpdateMode.Merge, ct);
                return new ForceBackfillJob(
                    job.PartitionKey, job.RowKey, job.GetString("RequestId") ?? string.Empty, asOfDate, attempts + 1);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogInformation("[Backfill] Job {Job} changed before it could be claimed - skipped.", job.RowKey);
            }
        }

        return null;
    }

    public Task CompleteAsync(
        ForceBackfillJob job, DateTimeOffset uploadedAt, int rows, string uploadUrl, CancellationToken ct = default)
        => TryUpdateAsync(job.DatimId, job.RowKey, new()
        {
            ["Status"] = "uploaded",
            ["CompletedAt"] = DateTimeOffset.UtcNow,
            ["UploadedAt"] = uploadedAt,
            ["Rows"] = rows,
            ["UploadUrl"] = uploadUrl,
            ["BlobPath"] = BlobPathOf(uploadUrl),
            ["Error"] = string.Empty,
        }, ct);

    public Task FailAsync(ForceBackfillJob job, string error, CancellationToken ct = default)
        => TryUpdateAsync(job.DatimId, job.RowKey, new()
        {
            ["Status"] = "failed",
            ["CompletedAt"] = DateTimeOffset.UtcNow,
            ["Error"] = error.Length > 1000 ? error[..1000] : error,
        }, ct);

    // Unconditional merge with a few retries. A job left running because every attempt failed is
    // picked up again by ReleaseInterruptedAsync at the next start.
    private async Task TryUpdateAsync(
        string datimId, string rowKey, Dictionary<string, object> fields, CancellationToken ct)
    {
        var patch = new TableEntity(datimId, rowKey);
        foreach (var (key, value) in fields)
            patch[key] = value;

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _table.UpdateEntityAsync(patch, ETag.All, TableUpdateMode.Merge, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[Backfill] Updating job {Job} failed (attempt {A}/{Max}): {Msg}",
                    rowKey, attempt, maxAttempts, ex.Message);
            }

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // https://<account>.blob.core.windows.net/<container>/RADET/2026-09-30/x.xlsx -> RADET/2026-09-30/x.xlsx
    // (also file:///C:/<local folder>/<container>/RADET/... from Storage:Azure:LocalUploadFolder)
    private string BlobPathOf(string uploadUrl)
    {
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri))
            return string.Empty;

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var marker = $"/{_containerName}/";
        var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? string.Empty : path[(at + marker.Length)..];
    }
}
