using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Licensing;

/// <summary>
/// Remembers the last registration result so BayCentral is asked about once a day: an authorised
/// result holds for the UTC day, a refusal for Auth:BlockedCacheMinutes. Kept in memory and, with
/// Auth:EnableDailyCache, in a JSON file (Auth:CachePath, default %LOCALAPPDATA%\AHNi-RSE\auth-cache.json)
/// signed with a key derived from the machine and licence key, so it cannot be copied or edited.
/// A result only applies to the same identity hash (device, DATIM ID, licence key).
/// </summary>
public sealed class AuthResultCache(IOptions<AuthSettings> options, ILogger<AuthResultCache> logger)
{
    private readonly AuthSettings _cfg = options.Value;

    private bool _deviceAuthorised;
    private DateOnly _deviceAuthDateUtc = DateOnly.MinValue;
    private DateTime _lastAuthCheckUtc = DateTime.MinValue;
    private string _identityHash = string.Empty;

    public bool IsInMemoryCacheHit(DateOnly todayUtc, DateTime nowUtc, string identityHash, out bool authorised)
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

    public void UpdateMemoryCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, bool authorised)
    {
        _deviceAuthDateUtc = todayUtc;
        _lastAuthCheckUtc = nowUtc;
        _identityHash = identityHash;
        _deviceAuthorised = authorised;
    }

    public bool TryReadDailyCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, out bool authorised)
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

    public void TryWriteDailyCache(DateOnly todayUtc, DateTime nowUtc, string identityHash, bool authorised)
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
        var machineGuid = DeviceIdentity.TryGetMachineGuid() ?? DeviceIdentity.UnknownIdentity;
        var seed = $"{machineGuid}|{_cfg.LicenseKey}|AHNi-RSE.AuthCache.v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    }

    private sealed class AuthCacheRecord
    {
        public string DateUtc { get; set; } = string.Empty;
        public bool Authorised { get; set; }
        public string CheckedAtUtc { get; set; } = string.Empty;
        public string IdentityHash { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
    }
}
