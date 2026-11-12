using System.Diagnostics;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using AHNiRSE.Infrastructure.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Application.Services;

/// <summary>
/// Executes all enabled scripts and aggregates metrics for heartbeat.
/// </summary>
public class ScriptManager(
    IEnumerable<IReportScript> scripts,
    IOptions<ReportSettings> options,
    ILogger<ScriptManager> logger)
{
    private readonly ReportSettings _settings = options.Value;

    public async Task<(int TotalRows, string UploadUrl)> RunEnabledAsync(
        CycleContext context,
        CancellationToken ct = default)
    {
        var enabledConfigs = _settings.Scripts
            .Where(s => s.Enabled)
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        if (enabledConfigs.Count == 0)
        {
            logger.LogWarning("[ScriptManager] No enabled scripts. Check Reports:Scripts in appsettings.json.");
            return (0, string.Empty);
        }

        var toRun = scripts.Where(s => enabledConfigs.ContainsKey(s.Name)).ToList();

        foreach (var missing in enabledConfigs.Keys
                     .Except(toRun.Select(s => s.Name), StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "[ScriptManager] Config has '{Script}' enabled but no IReportScript registered for it.", missing);
        }

        if (toRun.Count == 0)
        {
            logger.LogWarning("[ScriptManager] No runnable scripts found.");
            return (0, string.Empty);
        }

        var maxParallelScripts = Math.Clamp(_settings.MaxParallelScripts, 1, toRun.Count);

        logger.LogInformation(
            "[ScriptManager] RunDate={RunDate} | CycleId={CycleId} | Executing {Count} script(s) with MaxParallel={MaxParallel}: {Names}",
            context.RunDate.ToString("yyyy-MM-dd"),
            context.CycleId,
            toRun.Count,
            maxParallelScripts,
            string.Join(", ", toRun.Select(s => s.Name)));

        var runTasks = new List<Task<ScriptRunResult>>(toRun.Count);
        using var gate = new SemaphoreSlim(maxParallelScripts, maxParallelScripts);

        for (var i = 0; i < toRun.Count; i++)
        {
            var script = toRun[i];
            var cfg = enabledConfigs[script.Name];
            runTasks.Add(RunScriptWithGateAsync(i, script, cfg, context, gate, ct));
        }

        var results = await Task.WhenAll(runTasks);
        var ordered = results.OrderBy(r => r.Order).ToList();

        var totalRows = ordered.Sum(r => r.RowCount);
        var lastUrl = ordered
            .Select(r => r.UploadUrl)
            .LastOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
        var succeeded = ordered.Count(r => r.Status == ScriptRunStatus.Succeeded);
        var failed = ordered.Count(r => r.Status == ScriptRunStatus.Failed);
        var timedOut = ordered.Count(r => r.Status == ScriptRunStatus.TimedOut);

        logger.LogInformation(
            "[ScriptManager] SUMMARY | RunDate={RunDate} | Total={Total} OK={Ok} FAIL={Fail} TIMEOUT={Timeout} | {Rows} rows -> {Url}",
            context.RunDate.ToString("yyyy-MM-dd"),
            toRun.Count,
            succeeded,
            failed,
            timedOut,
            totalRows,
            lastUrl);

        MemoryPressureHelper.ReleaseAfterCycle(logger);

        return (totalRows, lastUrl);
    }

    private async Task<ScriptRunResult> RunScriptWithGateAsync(
        int order,
        IReportScript script,
        ScriptConfig cfg,
        CycleContext context,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await RunSingleScriptSafelyAsync(order, script, cfg, context, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ScriptRunResult> RunSingleScriptSafelyAsync(
        int order,
        IReportScript script,
        ScriptConfig cfg,
        CycleContext context,
        CancellationToken ct)
    {
        var timeout = Math.Max(30, cfg.TimeoutSeconds ?? _settings.DefaultScriptTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        var sw = Stopwatch.StartNew();
        try
        {
            logger.LogInformation(
                "[ScriptManager] -> {Script} | RunDate={RunDate} | Timeout={Timeout}s",
                script.Name, context.RunDate.ToString("yyyy-MM-dd"), timeout);

            var (rows, url) = await script.RunAsync(context, cts.Token);
            sw.Stop();

            logger.LogInformation(
                "[ScriptManager] OK {Script} | {Rows} rows | {Url} | {Ms}ms",
                script.Name, rows, url, sw.ElapsedMilliseconds);

            return new ScriptRunResult(order, rows, url, ScriptRunStatus.Succeeded);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            logger.LogError(
                "[ScriptManager] TIMEOUT {Script} after {Timeout}s ({Ms}ms elapsed)",
                script.Name, timeout, sw.ElapsedMilliseconds);
            return new ScriptRunResult(order, 0, string.Empty, ScriptRunStatus.TimedOut);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "[ScriptManager] FAIL {Script} ({Ms}ms): {Msg}",
                script.Name, sw.ElapsedMilliseconds, ex.Message);
            return new ScriptRunResult(order, 0, string.Empty, ScriptRunStatus.Failed);
        }
    }

    private enum ScriptRunStatus
    {
        Succeeded,
        Failed,
        TimedOut
    }

    private readonly record struct ScriptRunResult(
        int Order,
        int RowCount,
        string UploadUrl,
        ScriptRunStatus Status);
}

