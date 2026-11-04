using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AHNiRSE.Licensing;

public enum VerificationOutcome
{
    Authorized,
    Unauthorized,
    Inconclusive
}

/// <summary>
/// Asks BayCentral whether this PC is registered: POSTs { serialNumber, datim } to
/// Auth:ValidationEndpoint (/api/addon/verify-serial). A 2xx answer is read for a true/false flag
/// (success, authorized, valid, ...); 401 means not registered; 403, other errors, timeouts and
/// network failures are inconclusive. Uses the "InstallationAuthService" HttpClient configured in
/// Program.cs (HTTP/1.1 and Postman-like headers, so Cloudflare does not challenge it).
/// </summary>
public sealed class VerifySerialClient(
    IOptions<AuthSettings> options,
    IHttpClientFactory httpClientFactory,
    ILogger<VerifySerialClient> logger)
{
    private readonly AuthSettings _cfg = options.Value;

    public async Task<VerificationOutcome> VerifyAsync(string deviceId, string datimId, CancellationToken ct)
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

    private sealed class VerifyRequestPayload
    {
        [JsonPropertyName("serialNumber")]
        public string SerialNumber { get; set; } = string.Empty;

        [JsonPropertyName("datim")]
        public string Datim { get; set; } = string.Empty;
    }
}
