using System.Runtime.Versioning;
using System.ServiceProcess;

namespace AHNiRSE.Update;

/// <summary>
/// The updater: AHNiRSE-updater.exe --apply-update --install-dir D --staged S --version V --from F --service N
/// --health-minutes M. It is a copy of the running exe, started by <see cref="UpdateWorker"/> from
/// &lt;install&gt;\update, so it can replace &lt;install&gt;\AHNiRSE.exe. It:
///   1. stops the service and waits for AHNiRSE.exe to be free;
///   2. renames AHNiRSE.exe to AHNiRSE.previous.exe and copies the new exe in;
///   3. starts the service and waits up to M minutes for the new version's healthy marker (written after it has
///      run for a minute, see UpdateWorker);
///   4. if it doesn't come, puts AHNiRSE.previous.exe back, starts the service and records the version as failed,
///      so it is not tried again.
/// The outcome goes to update\last-result.json (the heartbeat's UpdateStatus) and logs\update-&lt;date&gt;.log.
/// </summary>
public static class UpdateApplier
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromMinutes(3);

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args) => Run(args, name => new WindowsService(name));

    /// <summary>Same as <see cref="Run(string[])"/> with the service control given (tests use a stand-in).</summary>
    public static int Run(string[] args, Func<string, IUpdateService> openService)
    {
        var a = ParseArgs(args);
        var installDir = a.GetValueOrDefault("install-dir", "");
        var staged = a.GetValueOrDefault("staged", "");
        var serviceName = a.GetValueOrDefault("service", "AHNi-RSE");
        var from = a.GetValueOrDefault("from", "");
        var healthMinutes = double.TryParse(a.GetValueOrDefault("health-minutes"), System.Globalization.CultureInfo.InvariantCulture, out var hm)
            ? hm
            : 5;
        if (installDir.Length == 0 || staged.Length == 0 || !System.Version.TryParse(a.GetValueOrDefault("version"), out var version))
        {
            Console.Error.WriteLine("Usage: AHNiRSE.exe --apply-update --install-dir <dir> --staged <exe> --version <v> [--from <v>] [--service <name>] [--health-minutes <n>]");
            return 2;
        }

        var log = new UpdateLog(installDir);
        var exe = Path.Combine(installDir, UpdateFiles.ExeName);
        var previous = Path.Combine(installDir, UpdateFiles.PreviousExeName);
        var healthy = UpdateFiles.HealthyMarker(installDir, version);

        void Result(string status, string error) =>
            UpdateFiles.WriteResult(installDir, new UpdateResult(version.ToString(), from, status, error, DateTimeOffset.UtcNow));

        log.Write($"Updating {serviceName} from {from} to {version} ({staged}).");
        try
        {
            if (!File.Exists(staged))
                throw new FileNotFoundException("The downloaded exe is missing.", staged);

            using var service = openService(serviceName);

            // 1. Stop the service and wait until its exe can be moved.
            StopService(service, log);
            MoveWhenFree(exe, previous);

            // 2. Put the new exe in place.
            try
            {
                File.Copy(staged, exe, overwrite: false);
            }
            catch (Exception ex)
            {
                log.Write($"Could not copy the new exe: {ex.Message}. Putting the previous one back.");
                File.Move(previous, exe, overwrite: true);
                StartService(service, log);
                Result("Failed", $"Could not copy the new exe: {ex.Message}");
                return 1;
            }

            // 3. Start it and wait for it to report healthy.
            TryDelete(healthy);
            var started = StartService(service, log);
            if (started && WaitForHealthy(service, healthy, TimeSpan.FromMinutes(healthMinutes), log))
            {
                log.Write($"{version} is running. Previous version kept as {UpdateFiles.PreviousExeName}.");
                Result("Installed", "");
                TryDelete(staged);
                return 0;
            }

            // 4. Roll back.
            var reason = started
                ? $"{version} did not keep running for a minute within {healthMinutes:0.#} min"
                : $"{version} did not start";
            log.Write($"{reason}. Rolling back to {from}.");
            StopService(service, log);
            MoveWhenFree(exe, Path.Combine(installDir, UpdateFiles.FailedExeName));
            File.Move(previous, exe, overwrite: true);
            UpdateFiles.AddFailedVersion(installDir, version);
            Result("RolledBack", reason);
            if (!StartService(service, log))
                log.Write($"WARNING: {from} did not start again after the rollback; Windows service recovery will retry it.");
            else
                log.Write($"Rolled back to {from}.");
            TryDelete(staged);
            return 1;
        }
        catch (Exception ex)
        {
            log.Write($"Update failed: {ex}");
            // Whatever happened, make sure there is an exe and the service is running.
            try
            {
                if (!File.Exists(exe) && File.Exists(previous))
                    File.Move(previous, exe);
                using var service = openService(serviceName);
                StartService(service, log);
            }
            catch (Exception inner)
            {
                log.Write($"Could not restart the service: {inner.Message}");
            }

            try { Result("Failed", ex.Message); } catch { /* nothing more to do */ }
            return 1;
        }
    }

    private static void StopService(IUpdateService service, UpdateLog log)
    {
        log.Write("Stopping the service.");
        service.Stop(ServiceTimeout);
    }

    /// <returns>False when it did not reach Running.</returns>
    private static bool StartService(IUpdateService service, UpdateLog log)
    {
        try
        {
            log.Write("Starting the service.");
            service.Start(ServiceTimeout);
            return true;
        }
        catch (Exception ex)
        {
            log.Write($"The service did not start: {ex.Message}");
            return false;
        }
    }

    /// <summary>The service process can take a moment to exit after it reports Stopped.</summary>
    private static void MoveWhenFree(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 60)
            {
                Thread.Sleep(1000);
            }
            catch (UnauthorizedAccessException) when (attempt < 60)
            {
                Thread.Sleep(1000);
            }
        }
    }

    private static bool WaitForHealthy(IUpdateService service, string marker, TimeSpan timeout, UpdateLog log)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(marker))
                return true;
            Thread.Sleep(2000);
        }

        log.Write($"No healthy marker after {timeout.TotalMinutes:0.#} min (service is {service.Status}).");
        return false;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                result[args[i][2..]] = args[++i];
        }
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>The RSE Windows service, through the Service Control Manager.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsService(string name) : IUpdateService
    {
        private readonly ServiceController _sc = new(name);

        public string Status
        {
            get { _sc.Refresh(); return _sc.Status.ToString(); }
        }

        public void Stop(TimeSpan timeout)
        {
            _sc.Refresh();
            if (_sc.Status == ServiceControllerStatus.Stopped)
                return;
            if (_sc.Status != ServiceControllerStatus.StopPending)
                _sc.Stop();
            _sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }

        public void Start(TimeSpan timeout)
        {
            _sc.Refresh();
            if (_sc.Status != ServiceControllerStatus.Running && _sc.Status != ServiceControllerStatus.StartPending)
                _sc.Start();
            _sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
        }

        public void Dispose() => _sc.Dispose();
    }

    /// <summary>&lt;install&gt;\logs\update-&lt;date&gt;.log, next to the service's own logs.</summary>
    private sealed class UpdateLog(string installDir)
    {
        private readonly string _path = Path.Combine(installDir, "logs", $"update-{DateTime.Now:yyyyMMdd}.log");

        public void Write(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Updater] {message}";
            Console.WriteLine(line);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* logging must never stop the update */ }
        }
    }
}

/// <summary>Start and stop of the service being updated (the Windows service, or a stand-in in tests).</summary>
public interface IUpdateService : IDisposable
{
    string Status { get; }

    /// <summary>Returns once it has stopped; throws if it doesn't within <paramref name="timeout"/>.</summary>
    void Stop(TimeSpan timeout);

    /// <summary>Returns once it is running; throws if it doesn't within <paramref name="timeout"/>.</summary>
    void Start(TimeSpan timeout);
}
