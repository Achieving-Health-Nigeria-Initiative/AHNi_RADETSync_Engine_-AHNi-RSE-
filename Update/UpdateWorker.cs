using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Update;

/// <summary>
/// Auto-update. Every AutoUpdate:CheckMinutes it reads &lt;Container&gt;/&lt;Folder&gt;/manifest.json
/// (autoreport/rse-release/manifest.json by default). When the manifest names a newer version (or a rollback)
/// for this facility (its DATIM id, or "all") and is not paused, it:
///   1. waits a random 0..SpreadMinutes, so facilities don't all download at once;
///   2. downloads the exe to &lt;install&gt;\update and checks its size, SHA-256 and file version against the manifest;
///   3. waits until no report cycle or force backfill is running and no scheduled cycle is due within
///      QuietMinutesBeforeSchedule, and takes the cycle lock so none starts;
///   4. starts a copy of the current exe as the updater (AHNiRSE-updater.exe --apply-update ..., see
///      <see cref="UpdateApplier"/>), which stops the service, swaps the exe, starts it again and puts the
///      previous exe back if the new one does not keep running.
/// A version rolled back here is not tried again. appsettings.json is never touched.
///
/// It also marks this version healthy a minute after start-up, which is what the updater waits for.
/// </summary>
public sealed class UpdateWorker(
    BlobServiceClient blobs,
    ReportOrchestrator orchestrator,
    FacilityRunState facility,
    UpdateStatus status,
    IOptions<AutoUpdateSettings> options,
    IOptions<StorageSettings> storage,
    IOptions<ReportSettings> reports,
    ILogger<UpdateWorker> logger) : RestartingWorker(logger)
{
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HandOffTimeout = TimeSpan.FromMinutes(10);

    private readonly AutoUpdateSettings _cfg = options.Value;
    private readonly string _installDir = UpdateFiles.InstallDir;
    private readonly Version _current = UpdateFiles.CurrentVersion;
    private bool _healthyMarked;

    // The version being waited for and when its download may start (the random spread).
    private Version? _spreadFor;
    private DateTimeOffset _spreadUntil;

    protected override string Name => "[Update]";

    private string ContainerName =>
        _cfg.Container is { Length: > 0 } c ? c
        : storage.Value.Azure.ScriptContainerName is { Length: > 0 } s ? s
        : storage.Value.Azure.ContainerName;

    private string BlobPath(string file) => $"{_cfg.Folder.Trim('/')}/{file}";

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!_healthyMarked)
        {
            await Task.Delay(HealthyAfter, ct);
            MarkHealthy();
            _healthyMarked = true;
        }

        if (!_cfg.Enabled)
        {
            logger.LogInformation("[Update] Auto-update is off (AutoUpdate:Enabled=false). Running {Version}.", _current);
            status.Set("Disabled");
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _cfg.CheckMinutes));
        logger.LogInformation(
            "[Update] Running {Version}. Checking {Container}/{Manifest} every {Minutes} min.",
            _current, ContainerName, BlobPath("manifest.json"), _cfg.CheckMinutes);

        while (!ct.IsCancellationRequested)
        {
            await CheckOnceAsync(ct);
            await Task.Delay(interval, ct);
        }
    }

    /// <summary>Tells the updater (if one is waiting) that this version started and kept running.</summary>
    private void MarkHealthy()
    {
        try
        {
            Directory.CreateDirectory(UpdateFiles.Dir(_installDir));
            File.WriteAllText(UpdateFiles.HealthyMarker(_installDir, _current), DateTimeOffset.UtcNow.ToString("o"));
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Update] Could not write the healthy marker: {Message}", ex.Message);
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var manifest = await ReadManifestAsync(ct);
            status.Checked();
            if (manifest is null)
            {
                status.Set("UpToDate");
                return;
            }

            if (Decide(manifest) is not { } target)
                return;

            if (!await SpreadElapsedAsync(target, ct))
                return;

            status.Set("Downloading", target.ToString());
            var staged = await DownloadAsync(manifest, target, ct);

            await InstallWhenIdleAsync(staged, target, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("[Update] Update check failed: {Message}", ex.Message);
            status.Set("CheckFailed", status.Snapshot().TargetVersion, ex.Message);
        }
    }

    private async Task<UpdateManifest?> ReadManifestAsync(CancellationToken ct)
    {
        var blob = blobs.GetBlobContainerClient(ContainerName).GetBlobClient(BlobPath("manifest.json"));
        try
        {
            var content = await blob.DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<UpdateManifest>(content.Value.Content.ToString(), UpdateManifest.Json)
                   ?? throw new InvalidDataException("manifest.json is empty.");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // nothing published yet
        }
    }

    /// <summary>The version to install, or null (with the reason in the status) when there is nothing to do.</summary>
    private Version? Decide(UpdateManifest m)
    {
        if (!System.Version.TryParse(m.Version, out var target))
            throw new InvalidDataException($"manifest.json has an invalid version '{m.Version}'.");

        if (target == _current || (target < _current && !m.Rollback))
        {
            status.Set("UpToDate", m.Version);
            return null;
        }

        if (m.Paused)
        {
            status.Set("Paused", m.Version);
            return null;
        }

        var datimId = facility.Snapshot().DatimId;
        if (!m.IsFor(datimId))
        {
            status.Set("NotInRollout", m.Version);
            return null;
        }

        if (UpdateFiles.ReadFailedVersions(_installDir).Contains(target.ToString()))
        {
            status.Set("RolledBack", m.Version, $"{m.Version} was rolled back on this server; waiting for a newer release.");
            return null;
        }

        return target;
    }

    private async Task<bool> SpreadElapsedAsync(Version target, CancellationToken ct)
    {
        if (_spreadFor != target)
        {
            _spreadFor = target;
            var minutes = Math.Max(0, _cfg.SpreadMinutes);
            _spreadUntil = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(0, minutes * 60 + 1));
            logger.LogInformation(
                "[Update] Version {Target} is available (running {Current}); downloading after {At:HH:mm:ss} UTC.",
                target, _current, _spreadUntil);
        }

        var wait = _spreadUntil - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return true;

        status.Set("Waiting", target.ToString());
        // A wait shorter than the check interval is done now rather than at the next check.
        if (wait > TimeSpan.FromMinutes(Math.Max(1, _cfg.CheckMinutes)))
            return false;
        await Task.Delay(wait, ct);
        return true;
    }

    /// <summary>Downloads (or reuses) &lt;install&gt;\update\AHNiRSE-&lt;version&gt;.exe and checks it against the manifest.</summary>
    private async Task<string> DownloadAsync(UpdateManifest m, Version target, CancellationToken ct)
    {
        var staged = UpdateFiles.StagedExe(_installDir, target);
        Directory.CreateDirectory(UpdateFiles.Dir(_installDir));

        if (File.Exists(staged) && Verify(staged, m, target) is null)
            return staged;

        var partial = staged + ".download";
        var blob = blobs.GetBlobContainerClient(ContainerName).GetBlobClient(BlobPath(m.File));
        logger.LogInformation("[Update] Downloading {Blob} ({Size:N0} bytes).", blob.Name, m.Size);
        await blob.DownloadToAsync(partial, ct);

        if (Verify(partial, m, target) is { } problem)
        {
            TryDelete(partial);
            throw new InvalidDataException($"Downloaded {m.File} was rejected: {problem}.");
        }

        File.Move(partial, staged, overwrite: true);
        logger.LogInformation("[Update] {File} downloaded and verified (SHA-256 and version match).", m.File);
        return staged;
    }

    /// <summary>Null when the file is the manifest's exe; otherwise what is wrong with it.</summary>
    public static string? Verify(string path, UpdateManifest m, Version target)
    {
        var info = new FileInfo(path);
        if (m.Size > 0 && info.Length != m.Size)
            return $"size {info.Length} instead of {m.Size}";

        string hash;
        using (var stream = File.OpenRead(path))
            hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            return "SHA-256 does not match the manifest";

        var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!System.Version.TryParse(fileVersion, out var v) || v != target)
            return $"file version {fileVersion} instead of {target}";

        return null;
    }

    private async Task InstallWhenIdleAsync(string staged, Version target, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
        {
            logger.LogInformation("[Update] {Target} is downloaded but not installed: not running as the Windows service.", target);
            status.Set("Downloaded", target.ToString(), "Not running as the Windows service; install it manually.");
            return;
        }

        if (MinutesToNextSchedule() is { } minutes && minutes < _cfg.QuietMinutesBeforeSchedule)
        {
            status.Set("WaitingForIdle", target.ToString(), $"A scheduled report cycle is due in {minutes:0} min.");
            return;
        }

        if (!orchestrator.TryHoldForUpdate())
        {
            status.Set("WaitingForIdle", target.ToString(), "A report cycle or force backfill is running.");
            return;
        }

        try
        {
            var updater = UpdateFiles.UpdaterExe(_installDir);
            File.Copy(Path.Combine(_installDir, UpdateFiles.ExeName), updater, overwrite: true);
            TryDelete(UpdateFiles.HealthyMarker(_installDir, target));

            var psi = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = UpdateFiles.Dir(_installDir),
            };
            foreach (var arg in new[]
                     {
                         UpdateFiles.ApplyUpdateArg,
                         "--install-dir", _installDir.TrimEnd('\\', '/'),
                         "--staged", staged,
                         "--version", target.ToString(),
                         "--from", _current.ToString(),
                         "--service", "AHNi-RSE",
                         "--health-minutes", Math.Max(2, _cfg.HealthCheckMinutes).ToString(),
                     })
                psi.ArgumentList.Add(arg);

            logger.LogInformation("[Update] Installing {Target} (running {Current}): handing over to the updater.", target, _current);
            status.Set("Installing", target.ToString());
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("The updater did not start.");
        }
        catch (Exception ex)
        {
            orchestrator.ReleaseUpdateHold();
            throw new InvalidOperationException($"Could not start the updater: {ex.Message}", ex);
        }

        // The updater stops this service. If it hasn't after HandOffTimeout something went wrong there, so
        // reports carry on and the next check tries again (its log is <install>\logs\update-<date>.log).
        await Task.Delay(HandOffTimeout, ct);
        orchestrator.ReleaseUpdateHold();
        var result = UpdateFiles.ReadResult(_installDir);
        var error = result is { Status: "Failed" } && result.Version == target.ToString()
            ? result.Error
            : "The updater did not restart the service.";
        logger.LogWarning("[Update] Installing {Target} did not complete: {Error}", target, error);
        status.Set("Failed", target.ToString(), error);
    }

    private double? MinutesToNextSchedule()
    {
        try
        {
            // ScheduleWorker already logged any problem with the schedule settings.
            var schedule = CronSchedule.FromSettings(reports.Value, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            return schedule.Next(DateTime.UtcNow) is { } next ? (next.NextUtc - DateTime.UtcNow).TotalMinutes : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
