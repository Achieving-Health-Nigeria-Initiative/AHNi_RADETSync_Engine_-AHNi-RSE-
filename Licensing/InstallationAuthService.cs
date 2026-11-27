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
/// A failed check, and the first pass after it, are also recorded in AuthFailureLog's table (DATIM ID + serial).
/// </summary>
public class InstallationAuthService(
    IOptions<AuthSettings> options,
    IDatabaseService database,
    IHostEnvironment hostEnvironment,
    DeviceIdentity device,
    AuthResultCache cache,
    VerifySerialClient verifier,
    AuthFailureLog failureLog,
    ILogger<InstallationAuthService> logger)
{
    private readonly AuthSettings _cfg = options.Value;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    /// <summary>Why the last check failed, in one line (empty once a check passes). Logged when a cycle is blocked.</summary>
    public string LastFailure { get; private set; } = string.Empty;

    /// <summary>The last check failed: ScheduleWorker re-checks every Auth:RetryWhenBlockedMinutes until it passes.</summary>
    public bool IsBlocked => LastFailure.Length > 0;

    public async Task<bool> ValidateInstallationAsync(CancellationToken ct = default)
    {
        var authorised = await ValidateCoreAsync(ct);
        if (authorised)
            LastFailure = string.Empty;
        return authorised;
    }

    private async Task<bool> ValidateCoreAsync(CancellationToken ct)
    {
        var authEnabled = _cfg.Enabled ?? !hostEnvironment.IsDevelopment();

        if (!authEnabled)
        {
            logger.LogWarning("[Auth] Validation disabled for this environment/config.");
            return true;
        }

        var facility = ResolveFacility();
        var datimId = facility.DatimId;
        var deviceId = device.ResolveDeviceId();

        if (string.IsNullOrWhiteSpace(_cfg.ValidationEndpoint))
        {
            logger.LogError("[Auth] ValidationEndpoint is missing. Set Auth:ValidationEndpoint to enable verification.");
            LogBlocked(facility, deviceId, "endpoint-missing");
            return false;
        }

        if (device.IsEnterpriseTier() && string.Equals(deviceId, DeviceIdentity.UnknownIdentity, StringComparison.Ordinal))
        {
            logger.LogError("[Auth] Enterprise validation failed: BIOS serial number is unavailable.");
            LogBlocked(facility, deviceId, "serial-unavailable");
            await failureLog.RecordFailureAsync(facility, deviceId, "serial-unavailable");
            return false;
        }

        // No DATIM ID (EMR database not reachable, e.g. PostgreSQL still starting after a reboot): BayCentral would
        // only refuse it. Blocked with the real reason and re-checked like any block; not written to the failure
        // table, as it is not a registration problem.
        if (string.Equals(datimId, DeviceIdentity.UnknownIdentity, StringComparison.Ordinal))
        {
            LogBlocked(facility, deviceId, "datim-unavailable");
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
                    await failureLog.RecordPassAsync(facility, deviceId);
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
                    LogBlocked(facility, deviceId, "refused");
                    await failureLog.RecordFailureAsync(facility, deviceId, "refused");
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
                    LogBlocked(facility, deviceId, "inconclusive");
                    await failureLog.RecordFailureAsync(facility, deviceId, "inconclusive");
                    return false;
            }
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// A banner that stands out in the console (exe run directly) and the log file: the RSE is blocked, why, and
    /// what to fix - the PC serial and DATIM ID to register in BayCentral's Serial Numbers.
    /// </summary>
    private void LogBlocked(Facility facility, string deviceId, string reason)
    {
        var (why, fix) = reason switch
        {
            "refused" => ("BayCentral has no registration for this PC's serial with this facility's DATIM ID.",
                          "In BayCentral > EMR Version Control > Serial Numbers, add or correct this PC's serial for this DATIM ID."),
            "serial-unavailable" => ("This PC's BIOS serial number could not be read, so it cannot be checked.",
                                     "Run the service as LocalSystem (Install.bat does) and check that '(Get-CimInstance Win32_BIOS).SerialNumber' shows one."),
            "datim-unavailable" => ("This facility's DATIM ID could not be read from the EMR database, so this PC cannot be checked.",
                                    "Check that PostgreSQL is running and the Database connection in appsettings.json is right; the RSE retries by itself."),
            "endpoint-missing" => ("Auth:ValidationEndpoint is not set in appsettings.json, so this PC cannot be checked.",
                                   "Set Auth:ValidationEndpoint to BayCentral's verify-serial address and restart the service."),
            _ => ("BayCentral could not be reached to check this PC.",
                  "Check the internet connection, or set Auth:PermitWhenInconclusive=true."),
        };
        var title = reason is "refused" or "serial-unavailable"
            ? "THIS PC IS NOT AUTHORISED"
            : "THE LICENCE CHECK COULD NOT BE DONE";
        var facilityName = string.IsNullOrWhiteSpace(facility.FacilityName) ? "(name not read)" : facility.FacilityName;
        var failure = $"{why} Facility {facilityName}, DATIM {facility.DatimId}, PC serial {deviceId}.";
        if (failure == LastFailure)
        {
            // Same failure as the last check (the 5-minute re-checks): one line, not the whole banner again.
            logger.LogWarning("[Auth] Still blocked - {Failure}", failure);
            return;
        }
        LastFailure = failure;

        const string Line = "==================================================================";
        logger.LogError(
            "\n" + Line + "\n" +
            "  AHNi-RSE IS BLOCKED - {Title}\n" +
            "  {Why}\n" +
            "    Facility  : {Facility}\n" +
            "    DATIM ID  : {Datim}\n" +
            "    PC serial : {Serial}\n" +
            "  No reports are generated or uploaded until this is fixed.\n" +
            "  Fix: {Fix}\n" +
            "  The RSE checks again every few minutes and runs the report as soon as this PC is accepted.\n" +
            Line,
            title,
            why,
            string.IsNullOrWhiteSpace(facility.FacilityName) ? "(not read from the database)" : facility.FacilityName,
            facility.DatimId, deviceId, fix);
    }

    /// <summary>The facility from the EMR database; DATIM ID UNKNOWN when it cannot be read.</summary>
    private Facility ResolveFacility()
    {
        try
        {
            var facility = database.GetFacilityInfo();
            if (!string.IsNullOrWhiteSpace(facility.DatimId))
            {
                return new Facility { FacilityName = facility.FacilityName, DatimId = facility.DatimId.Trim(), State = facility.State };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Auth] Could not resolve DATIM ID from database: {Message}", ex.Message);
        }

        return new Facility { DatimId = DeviceIdentity.UnknownIdentity };
    }

    private static string BuildIdentityHash(string deviceId, string datimId, string licenseKey)
    {
        var raw = $"{deviceId}|{datimId}|{licenseKey}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }
}
