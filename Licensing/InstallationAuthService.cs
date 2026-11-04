using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Licensing;

/// <summary>
/// The registration check run before every report cycle and force backfill: is this PC (BIOS serial,
/// see DeviceIdentity) registered in BayCentral for this facility's DATIM ID?
///
/// Auth:Enabled=false (or unset in Development) skips the check. Otherwise the result comes from
/// AuthResultCache when it still holds, else from BayCentral (VerifySerialClient). An inconclusive
/// answer (firewall, timeout, network) counts as authorised only when Auth:PermitWhenInconclusive is true.
/// </summary>
public class InstallationAuthService(
    IOptions<AuthSettings> options,
    IDatabaseService database,
    IHostEnvironment hostEnvironment,
    DeviceIdentity device,
    AuthResultCache cache,
    VerifySerialClient verifier,
    ILogger<InstallationAuthService> logger)
{
    private readonly AuthSettings _cfg = options.Value;
    private readonly SemaphoreSlim _authLock = new(1, 1);

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
        var deviceId = device.ResolveDeviceId();
        if (device.IsEnterpriseTier() && string.Equals(deviceId, DeviceIdentity.UnknownIdentity, StringComparison.Ordinal))
        {
            logger.LogError("[Auth] Enterprise validation failed: BIOS serial number is unavailable.");
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var todayUtc = DateOnly.FromDateTime(nowUtc);
        var identityHash = BuildIdentityHash(deviceId, datimId, _cfg.LicenseKey);

        if (cache.IsInMemoryCacheHit(todayUtc, nowUtc, identityHash, out var cachedInMemory))
        {
            logger.LogDebug("[Auth] In-memory auth cache hit for DATIM={DatimId}.", datimId);
            return cachedInMemory;
        }

        await _authLock.WaitAsync(ct);
        try
        {
            if (cache.IsInMemoryCacheHit(todayUtc, nowUtc, identityHash, out cachedInMemory))
            {
                return cachedInMemory;
            }

            if (_cfg.EnableDailyCache &&
                cache.TryReadDailyCache(todayUtc, nowUtc, identityHash, out var cachedFromDisk))
            {
                if (cachedFromDisk)
                {
                    cache.UpdateMemoryCache(todayUtc, nowUtc, identityHash, true);
                    logger.LogInformation("[Auth] Auth cache hit from disk. Authorised=True.");
                    return true;
                }

                logger.LogInformation("[Auth] Disk cache says Authorised=False. Re-validating with server.");
            }

            var outcome = await verifier.VerifyAsync(deviceId, datimId, ct);
            switch (outcome)
            {
                case VerificationOutcome.Authorized:
                    cache.UpdateMemoryCache(todayUtc, nowUtc, identityHash, true);
                    if (_cfg.EnableDailyCache)
                    {
                        cache.TryWriteDailyCache(todayUtc, nowUtc, identityHash, true);
                    }

                    logger.LogInformation(
                        "[Auth] Daily verification completed. Authorised=True, DATIM={DatimId}, Device={Device}.",
                        datimId,
                        deviceId);
                    return true;

                case VerificationOutcome.Unauthorized:
                    cache.UpdateMemoryCache(todayUtc, nowUtc, identityHash, false);
                    if (_cfg.EnableDailyCache)
                    {
                        cache.TryWriteDailyCache(todayUtc, nowUtc, identityHash, false);
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
                            "[Auth] Verification inconclusive (WAF/proxy/timeout) — PermitWhenInconclusive=true, allowing cycle. DATIM={DatimId}, Device={Device}.",
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

        return DeviceIdentity.UnknownIdentity;
    }

    private static string BuildIdentityHash(string deviceId, string datimId, string licenseKey)
    {
        var raw = $"{deviceId}|{datimId}|{licenseKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }
}
