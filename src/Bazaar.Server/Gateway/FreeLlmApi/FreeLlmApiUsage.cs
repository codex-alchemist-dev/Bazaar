using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>Request and token totals for one platform in the month, from FreeLLMAPI's <c>GET /v1/usage</c>.</summary>
public sealed record FreeLlmApiPlatformUsage(string Platform, long Requests, long Tokens);

/// <summary>
/// Usage and caps for one FreeLLMAPI key. There is deliberately no label field: the label is operator text and is not stored.
/// </summary>
public sealed record FreeLlmApiKeyUsage(long KeyId, string Platform, bool Enabled, long Requests, long Tokens, long? RequestCap, long? TokenCap);

/// <summary>
/// The month's usage as FreeLLMAPI reports it. <c>ResetsAt</c> is null when the upstream omits it or it does not parse.
/// </summary>
public sealed record FreeLlmApiUsageReport(
    string Month,
    DateTimeOffset? ResetsAt,
    long Requests,
    long Tokens,
    IReadOnlyList<FreeLlmApiPlatformUsage> ByPlatform,
    IReadOnlyList<FreeLlmApiKeyUsage> ByKey);

/// <summary>The outcome of one usage read. Exactly one of <c>Report</c> and <c>Failure</c> is set.</summary>
public sealed record FreeLlmApiUsageResult(FreeLlmApiUsageReport? Report, string? Failure);

/// <summary>
/// Parses FreeLLMAPI's usage body by hand. Any missing or wrong-typed required field, or invalid JSON, gives null.
/// Numbers must be integers that fit a <see cref="long"/>. Unknown properties are ignored.
/// </summary>
public static class FreeLlmApiUsageParser
{
    public static FreeLlmApiUsageReport? TryParse(string json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseReport(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FreeLlmApiUsageReport? ParseReport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryString(root, "month", out var month) ||
            !TryLong(root, "requests", out var requests) ||
            !TryLong(root, "tokens", out var tokens) ||
            !TryArray(root, "byPlatform", out var platformRows) ||
            !TryArray(root, "byKey", out var keyRows))
        {
            return null;
        }

        var byPlatform = new List<FreeLlmApiPlatformUsage>();
        foreach (var row in platformRows.EnumerateArray())
        {
            if (!TryString(row, "platform", out var platform) ||
                !TryLong(row, "requests", out var platformRequests) ||
                !TryLong(row, "tokens", out var platformTokens))
            {
                return null;
            }

            byPlatform.Add(new FreeLlmApiPlatformUsage(platform, platformRequests, platformTokens));
        }

        var byKey = new List<FreeLlmApiKeyUsage>();
        foreach (var row in keyRows.EnumerateArray())
        {
            if (!TryLong(row, "keyId", out var keyId) ||
                !TryString(row, "platform", out var keyPlatform) ||
                !TryBool(row, "enabled", out var enabled) ||
                !TryLong(row, "requests", out var keyRequests) ||
                !TryLong(row, "tokens", out var keyTokens) ||
                !TryNullableLong(row, "requestCap", out var requestCap) ||
                !TryNullableLong(row, "tokenCap", out var tokenCap))
            {
                return null;
            }

            byKey.Add(new FreeLlmApiKeyUsage(keyId, keyPlatform, enabled, keyRequests, keyTokens, requestCap, tokenCap));
        }

        return new FreeLlmApiUsageReport(month, ParseResetsAt(root), requests, tokens, byPlatform, byKey);
    }

    private static DateTimeOffset? ParseResetsAt(JsonElement root)
    {
        if (!root.TryGetProperty("resetsAt", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetsAt)
            ? resetsAt
            : null;
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        return obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    /// <summary>A required property whose value is an integer or JSON null.</summary>
    private static bool TryNullableLong(JsonElement obj, string name, out long? value)
    {
        value = null;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            value = number;
            return true;
        }

        return false;
    }

    private static bool TryBool(JsonElement obj, string name, out bool value)
    {
        value = false;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryArray(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        value = element;
        return true;
    }
}

/// <summary>
/// Reads <c>GET {BaseUrl}/usage</c> from FreeLLMAPI. Upstream problems come back as a <see cref="FreeLlmApiUsageResult"/>
/// failure and never as an exception. The failure text is a fixed phrase or a status code, never a body, header or key.
/// The injected <see cref="HttpClient"/> must have its <see cref="HttpClient.Timeout"/> set.
/// </summary>
public sealed class FreeLlmApiUsageClient(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<FreeLlmApiUsageResult> GetAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = BuildRequest(endpoint);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var report = FreeLlmApiUsageParser.TryParse(body);
            return report is null ? Failed("malformed usage body") : new FreeLlmApiUsageResult(report, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("timeout");
        }
        catch (HttpRequestException)
        {
            return Failed("unreachable");
        }
    }

    private static FreeLlmApiUsageResult Failed(string failure) => new(null, failure);

    private static HttpRequestMessage BuildRequest(ProviderEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/usage", UriKind.Absolute));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var overridesAuthorization = false;
        if (endpoint.ExtraHeaders is not null)
        {
            foreach (var header in endpoint.ExtraHeaders)
            {
                overridesAuthorization |= string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (!string.IsNullOrEmpty(endpoint.ApiKey) && !overridesAuthorization)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        return request;
    }
}
