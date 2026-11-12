using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AHNiRSE.Configuration;
using AHNiRSE.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AHNiRSE.Infrastructure.Auth;

public class InstallationAuthService(
    IOptions<AuthSettings> options,
    IDatabaseService database,
    IHttpClientFactory httpClientFactory,
    IHostEnvironment hostEnvironment,
    ILogger<InstallationAuthService> logger) : IAuthService
{
    private enum VerificationOutcome
    {
        Authorized,
        Unauthorized,
        Inconclusive
    }

    private const string UnknownIdentity = "UNKNOWN";

    private readonly AuthSettings _cfg = options.Value;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    private bool _deviceAuthorised;
    private DateOnly _deviceAuthDateUtc = DateOnly.MinValue;
    private DateTime _lastAuthCheckUtc = DateTime.MinValue;
    private string _identityHash = string.Empty;
    private string? _deviceId;

    public async Task<bool> ValidateInstallationAsync(CancellationToken ct = default)
    {
        var authEnabled = _cfg.Enabled ?? !hostEnvironment.IsDevelopment();

        if (!authEnabled)
        {
            logger.LogWarning("[Auth] Validation disabled for this environment/config.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(_cfg.ValidationEndpoint))
        {
            logger.LogError("[Auth] ValidationEndpoint is missing. Set Auth:ValidationEndpoint to enable verification.");
            return false;
        }

        var datimId = ResolveDatimId();
        var deviceId = ResolveDeviceId();
        if (IsEnterpriseTier() && string.Equals(deviceId, UnknownIdentity, StringComparison.Ordinal))
        {
            logger.LogError("[Auth] Enterprise validation failed: BIOS serial number is unavailable.");
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var todayUtc = DateOnly.FromDateTime(nowUtc);
        var identityHash = BuildIdentityHash(deviceId, datimId, _cfg.LicenseKey);

        if (IsInMemoryCacheHit(todayUtc, nowUtc, identityHash, out var cachedInMemory))
        {
            logger.LogDebug("[Auth] In-memory auth cache hit for DATIM={DatimId}.", datimId);
            return cachedInMemory;
        }

        await _authLock.WaitAsync(ct);
        try
        {
            if (IsInMemoryCacheHit(todayUtc, nowUtc, identityHash, out cachedInMemory))
            {
                return cachedInMemory;
            }

            if (_cfg.EnableDailyCache &&
                TryReadDailyCache(todayUtc, nowUtc, identityHash, out var cachedFromDisk))
            {
                if (cachedFromDisk)
                {
                    UpdateMemoryCache(todayUtc, nowUtc, identityHash, true);
                    logger.LogInformation("[Auth] Auth cache hit from disk. Authorised=True.");
                    return true;
                }

                logger.LogInformation("[Auth] Disk cache says Authorised=False. Re-validating with server.");
            }

            var outcome = await VerifyWithServerAsync(deviceId, datimId, ct);
            switch (outcome)
            {
                case VerificationOutcome.Authorized:
                    UpdateMemoryCache(todayUtc, nowUtc, identityHash, true);
                    if (_cfg.EnableDailyCache)
                    {
                        TryWriteDailyCache(todayUtc, nowUtc, identityHash, true);
                    }

                    logger.LogInformation(
                        "[Auth] Daily verification completed. Authorised=True, DATIM={DatimId}, Device={Device}.",
                        datimId,
                        deviceId);
                    return true;

                case VerificationOutcome.Unauthorized:
                    UpdateMemoryCache(todayUtc, nowUtc, identityHash, false);
                    if (_cfg.EnableDailyCache)
                    {
                        TryWriteDailyCache(todayUtc, nowUtc, identityHash, false);
                    }

                    logger.LogInformation(
                        "[Auth] Daily verification completed. Authorised=False, DATIM={DatimId}, Device={Device}.",
                        datimId,
                        deviceId);
                    return false;

                default:
                    if (_cfg.PermitWhenInconclusive)
                    {
                        logger.LogWarning(
                            "[Auth] Verification inconclusive (WAF/proxy/timeout) â€” PermitWhenInconclusive=true, allowing cycle. DATIM={DatimId}, Device={Device}.",
                            datimId,
                            deviceId);
                        return true;
                    }

                    logger.LogWarning(
                        "[Auth] Verification was inconclusive (transport/proxy/upstream). No negative cache will be written. DATIM={DatimId}, Device={Device}.",
                        datimId,
                        deviceId);
                    return false;
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<VerificationOutcome> VerifyWithServerAsync(string deviceId, string datimId, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _cfg.RequestTimeoutSeconds)));

            using var client = httpClientFactory.CreateClient(nameof(InstallationAuthService));
            client.Timeout = Timeout.InfiniteTimeSpan;

            var payload = new VerifyRequestPayload
            {
                SerialNumber = deviceId,
                Datim = datimId
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, _cfg.ValidationEndpoint)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning(
                        "[Auth] Verification endpoint returned 401 (Unauthorized). Device is not authorised. Body={BodySnippet}",
                        ToLogSnippet(body));
                    return VerificationOutcome.Unauthorized;
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(
                        "[Auth] Verification endpoint returned 403 (Forbidden). This usually indicates upstream middleware/proxy/WAF denial before controller auth logic. Body={BodySnippet}",
                        ToLogSnippet(body));
                    return VerificationOutcome.Inconclusive;
                }
                else
                {
                    logger.LogWarning(
                        "[Auth] Verification endpoint returned {StatusCode} ({Reason}). Body={BodySnippet}",
                        (int)response.StatusCode,
                        response.ReasonPhrase ?? "n/a",
                        ToLogSnippet(body));
                    return VerificationOutcome.Inconclusive;
                }
            }

            if (!TryExtractAuthorization(body, out var authorised))
            {
                logger.LogWarning("[Auth] Verification response could not be parsed. Device considered not authorised.");
                return VerificationOutcome.Unauthorized;
            }

            return authorised ? VerificationOutcome.Authorized : VerificationOutcome.Unauthorized;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogError("[Auth] Verification timed out after {TimeoutSeconds}s.", Math.Max(5, _cfg.RequestTimeoutSeconds));
            return VerificationOutcome.Inconclusive;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Auth] Verification request failed: {Message}", ex.Message);
            return VerificationOutcome.Inconclusive;
        }
    }

    private string ResolveDatimId()
    {
        try
        {
            var facility = database.GetFacilityInfo();
            if (!string.IsNullOrWhiteSpace(facility.DatimId))
            {
                return facility.DatimId.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Could not resolve DATIM ID from database: {Message}", ex.Message);
        }

        return UnknownIdentity;
    }

    private string ResolveDeviceId()
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

    private static string BuildIdentityHash(string deviceId, string datimId, string licenseKey)
    {
        var raw = $"{deviceId}|{datimId}|{licenseKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    private bool IsInMemoryCacheHit(DateOnly todayUtc, DateTime nowUtc, string identityHash, out bool authorised)
    {
        if (!string.Equals(_identityHash, identityHash, StringComparison.Ordinal))
        {
            authorised = false;
            return false;
        }

        if (_deviceAuthorised && _deviceAuthDateUtc == todayUtc)
        {
            authorised = _deviceAuthorised;
            return true;
        }

        if (!_deviceAuthorised && IsBlockedCacheStillValid(_lastAuthCheckUtc, nowUtc))
        {
            authorised = false;
            return true;
        }

        authorised = false;
        return false;
    }

    private void UpdateMemoryCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, bool authorised)
    {
        _deviceAuthDateUtc = todayUtc;
        _lastAuthCheckUtc = nowUtc;
        _identityHash = identityHash;
        _deviceAuthorised = authorised;
    }

    private bool TryReadDailyCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, out bool authorised)
    {
        authorised = false;

        try
        {
            var path = ResolveCachePath();
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize<AuthCacheRecord>(json);
            if (record is null)
            {
                return false;
            }

            if (!string.Equals(record.IdentityHash, identityHash, StringComparison.Ordinal))
            {
                return false;
            }

            if (!IsCacheSignatureValid(record))
            {
                logger.LogWarning("[Auth] Daily auth cache signature mismatch. Ignoring local cache.");
                return false;
            }

            if (record.Authorised)
            {
                if (!string.Equals(record.DateUtc, todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else
            {
                if (!DateTime.TryParse(
                        record.CheckedAtUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var checkedAtUtc))
                {
                    return false;
                }

                if (!IsBlockedCacheStillValid(checkedAtUtc, nowUtc))
                {
                    return false;
                }
            }

            authorised = record.Authorised;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Failed to read daily auth cache: {Message}", ex.Message);
            return false;
        }
    }

    private void TryWriteDailyCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, bool authorised)
    {
        try
        {
            var path = ResolveCachePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var record = new AuthCacheRecord
            {
                DateUtc = todayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Authorised = authorised,
                CheckedAtUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture),
                IdentityHash = identityHash
            };
            record.Signature = SignCacheRecord(record);

            var json = JsonSerializer.Serialize(record);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Failed to write daily auth cache: {Message}", ex.Message);
        }
    }

    private bool IsBlockedCacheStillValid(DateTime checkedAtUtc, DateTime nowUtc)
    {
        if (checkedAtUtc == DateTime.MinValue || checkedAtUtc > nowUtc)
        {
            return false;
        }

        var ttl = TimeSpan.FromMinutes(Math.Max(1, _cfg.BlockedCacheMinutes));
        return (nowUtc - checkedAtUtc) < ttl;
    }

    private string ResolveCachePath()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.CachePath))
        {
            return _cfg.CachePath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AHNi-RSE",
            "auth-cache.json");
    }

    private bool IsCacheSignatureValid(AuthCacheRecord record)
    {
        try
        {
            var expected = Convert.FromHexString(SignCacheRecord(record));
            var provided = Convert.FromHexString(record.Signature);
            return CryptographicOperations.FixedTimeEquals(expected, provided);
        }
        catch
        {
            return false;
        }
    }

    private string SignCacheRecord(AuthCacheRecord record)
    {
        var data = $"{record.DateUtc}|{(record.Authorised ? 1 : 0)}|{record.CheckedAtUtc}|{record.IdentityHash}";
        var key = BuildCacheSigningKey();
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(signature);
    }

    private byte[] BuildCacheSigningKey()
    {
        var machineGuid = TryGetMachineGuid() ?? UnknownIdentity;
        var seed = $"{machineGuid}|{_cfg.LicenseKey}|AHNi-RSE.AuthCache.v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private static string? TryGetMachineGuid()
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

    private bool IsEnterpriseTier()
    {
        return string.Equals(_cfg.LicenseTier, "Enterprise", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDeterministicFallbackDeviceId()
    {
        var raw = $"{Environment.MachineName}|{Environment.OSVersion.VersionString}|AHNi-RSE";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return $"FALLBACK-{Convert.ToHexString(hash)[..16]}";
    }

    private static bool TryExtractAuthorization(string payload, out bool authorised)
    {
        authorised = false;

        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        payload = payload.Trim();

        if (bool.TryParse(payload, out var directBool))
        {
            authorised = directBool;
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return TryExtractAuthorizationFromElement(doc.RootElement, out authorised);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractAuthorizationFromElement(JsonElement element, out bool authorised)
    {
        authorised = false;

        if (TryConvertElementToBool(element, out authorised))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new[]
            {
                "authorized",
                "authorised",
                "valid",
                "success",
                "isAuthorized",
                "isAuthorised",
                "isValid"
            };

            foreach (var key in keys)
            {
                if (element.TryGetProperty(key, out var value) &&
                    TryConvertElementToBool(value, out authorised))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryExtractAuthorizationFromElement(property.Value, out authorised))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryExtractAuthorizationFromElement(item, out authorised))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryConvertElementToBool(JsonElement element, out bool value)
    {
        value = false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out var numeric):
                value = numeric != 0;
                return true;
            case JsonValueKind.String:
                var str = element.GetString();
                if (bool.TryParse(str, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }

                if (int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    value = intValue != 0;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static string ToLogSnippet(string? body, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= maxLength)
        {
            return oneLine;
        }

        return $"{oneLine[..maxLength]}...";
    }

    private sealed class AuthCacheRecord
    {
        public string DateUtc { get; set; } = string.Empty;
        public bool Authorised { get; set; }
        public string CheckedAtUtc { get; set; } = string.Empty;
        public string IdentityHash { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }

    private sealed class VerifyRequestPayload
    {
        [JsonPropertyName("serialNumber")]
        public string SerialNumber { get; set; } = string.Empty;

        [JsonPropertyName("datim")]
        public string Datim { get; set; } = string.Empty;
    }
}

