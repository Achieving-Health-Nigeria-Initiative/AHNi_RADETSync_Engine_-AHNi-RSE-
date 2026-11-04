using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AHNiRSE.Licensing;

/// <summary>
/// The PC's identity sent to BayCentral's verify-serial check: the BIOS serial number, read through
/// System.Management, then wmic, then PowerShell (WMI, then CIM). The Enterprise tier requires the
/// BIOS serial; other tiers fall back to the board serial, the Windows MachineGuid or a hash of the
/// machine name. Read once and kept for the life of the service.
/// </summary>
public sealed class DeviceIdentity(IOptions<AuthSettings> options, ILogger<DeviceIdentity> logger)
{
    public const string UnknownIdentity = "UNKNOWN";

    private readonly AuthSettings _cfg = options.Value;
    private string? _deviceId;

    public string ResolveDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            return _deviceId;
        }

        var biosSerial =
            TryGetBiosSerialFromDotNetWmi() ??
            TryGetBiosSerialFromWmic() ??
            TryGetBiosSerialFromLegacyWmiPowerShell() ??
            TryGetBiosSerialFromCim();

        if (!string.IsNullOrWhiteSpace(biosSerial))
        {
            _deviceId = biosSerial.Trim();
            return _deviceId;
        }

        if (IsEnterpriseTier())
        {
            logger.LogWarning("[Auth] Enterprise tier requires BIOS serial number. Serial could not be resolved.");
            _deviceId = UnknownIdentity;
            return _deviceId;
        }

        var deviceId =
            TryGetBoardSerialFromWmic() ??
            TryGetBoardSerialFromCim() ??
            TryGetMachineGuid() ??
            BuildDeterministicFallbackDeviceId();

        _deviceId = deviceId.Trim();
        return _deviceId;
    }

    public bool IsEnterpriseTier()
    {
        return string.Equals(_cfg.LicenseTier, "Enterprise", StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryGetMachineGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null)?.ToString();

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetBiosSerialFromDotNetWmi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                var raw = obj["SerialNumber"]?.ToString();
                var serial = ExtractSerialFromCommandOutput(raw);
                if (!string.IsNullOrWhiteSpace(serial))
                {
                    return serial;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Auth] Unable to read BIOS serial via System.Management: {Message}", ex.Message);
            return null;
        }
    }

    private string? TryGetBiosSerialFromWmic()
    {
        var output = RunProcessAndReadStdout("wmic", "bios get serialnumber");
        return ExtractSerialFromCommandOutput(output);
    }

    private string? TryGetBiosSerialFromCim()
    {
        var output = RunProcessAndReadStdout(
            "powershell",
            "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_BIOS).SerialNumber\"");
        return ExtractSerialFromCommandOutput(output);
    }

    private string? TryGetBiosSerialFromLegacyWmiPowerShell()
    {
        var output = RunProcessAndReadStdout(
            "powershell",
            "-NoProfile -NonInteractive -Command \"Get-WmiObject -Class Win32_BIOS | Select-Object -Property SerialNumber\"");
        return ExtractSerialFromCommandOutput(output);
    }

    private string? TryGetBoardSerialFromWmic()
    {
        var output = RunProcessAndReadStdout("wmic", "baseboard get serialnumber");
        return ExtractSerialFromCommandOutput(output);
    }

    private string? TryGetBoardSerialFromCim()
    {
        var output = RunProcessAndReadStdout(
            "powershell",
            "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_BaseBoard).SerialNumber\"");
        return ExtractSerialFromCommandOutput(output);
    }

    private string? RunProcessAndReadStdout(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Auth] Unable to read serial via {Command}: {Message}", fileName, ex.Message);
            return null;
        }
    }

    private static string? ExtractSerialFromCommandOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                !line.Contains("serialnumber", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("to be filled by o.e.m.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var line in lines)
        {
            if (line.All(ch => ch is '-' or '_' or '=' or ':'))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line) &&
                !string.Equals(line, "unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(line, "system serial number", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(line, "default string", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(line, "none", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(line, "serialnumber", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        return null;
    }

    private static string BuildDeterministicFallbackDeviceId()
    {
        var raw = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}|AHNi-RSE";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"FALLBACK-{Convert.ToHexString(hash)[..16]}";
    }
}
