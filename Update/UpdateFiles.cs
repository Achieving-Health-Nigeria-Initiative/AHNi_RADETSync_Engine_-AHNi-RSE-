using System.Reflection;
using System.Text.Json;

namespace AHNiRSE.Update;

/// <summary>
/// &lt;install folder&gt;\update: the downloaded release, the updater copy and the files the service and the updater
/// use to tell each other how an update went.
/// </summary>
public static class UpdateFiles
{
    public const string ExeName = "AHNiRSE.exe";
    public const string PreviousExeName = "AHNiRSE.previous.exe";
    public const string FailedExeName = "AHNiRSE.failed.exe";
    public const string ApplyUpdateArg = "--apply-update";

    public static string InstallDir => AppContext.BaseDirectory;
    public static string Dir(string installDir) => Path.Combine(installDir, "update");

    public static string StagedExe(string installDir, Version v) => Path.Combine(Dir(installDir), $"AHNiRSE-{v}.exe");
    public static string UpdaterExe(string installDir) => Path.Combine(Dir(installDir), "AHNiRSE-updater.exe");

    /// <summary>Written by the service a minute after it started, so the updater knows the new version runs.</summary>
    public static string HealthyMarker(string installDir, Version v) => Path.Combine(Dir(installDir), $"healthy-{v}");

    /// <summary>How the last update went (written by the updater, read by the service for the heartbeat).</summary>
    public static string ResultFile(string installDir) => Path.Combine(Dir(installDir), "last-result.json");

    /// <summary>Versions that were rolled back here; they are not tried again.</summary>
    public static string FailedVersionsFile(string installDir) => Path.Combine(Dir(installDir), "failed-versions.txt");

    /// <summary>The running exe's version: the build's &lt;Version&gt; (yyyy.M.d.Hmm).</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(UpdateFiles).Assembly.GetName().Version ?? new Version(0, 0);

    public static UpdateResult? ReadResult(string installDir)
    {
        try
        {
            var path = ResultFile(installDir);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), UpdateManifest.Json)
                : null;
        }
        catch { return null; }
    }

    public static void WriteResult(string installDir, UpdateResult result)
    {
        Directory.CreateDirectory(Dir(installDir));
        File.WriteAllText(ResultFile(installDir), JsonSerializer.Serialize(result, UpdateManifest.Json));
    }

    public static HashSet<string> ReadFailedVersions(string installDir)
    {
        try
        {
            var path = FailedVersionsFile(installDir);
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
                : [];
        }
        catch { return []; }
    }

    public static void AddFailedVersion(string installDir, Version v)
    {
        Directory.CreateDirectory(Dir(installDir));
        File.AppendAllLines(FailedVersionsFile(installDir), [v.ToString()]);
    }
}

/// <summary>Outcome of one update: Installed, RolledBack or Failed (nothing changed).</summary>
public sealed record UpdateResult(string Version, string FromVersion, string Status, string Error, DateTimeOffset AtUtc);
