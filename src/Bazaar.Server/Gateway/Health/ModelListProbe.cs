using System.Net.Http.Headers;
using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.Health;

/// <summary>
/// Probes an OpenAI-style endpoint with a <c>GET {BaseUrl}/models</c> list call. A 2xx answer is healthy. Any other
/// status, a timeout or an unreachable upstream is unhealthy and does not throw. The injected <see cref="HttpClient"/>
/// must have its <see cref="HttpClient.Timeout"/> set, because this probe does not add its own deadline.
/// </summary>
public sealed class ModelListProbe : IHealthProbe
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public ModelListProbe(HttpClient http, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = time ?? TimeProvider.System;
    }

    public async Task<ProbeResult> ProbeAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = BuildRequest(endpoint);
        var started = _time.GetUtcNow();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var latency = _time.GetUtcNow() - started;
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new ProbeResult(true, latency, $"HTTP {status}")
                : new ProbeResult(false, latency, $"HTTP {status}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult(false, _time.GetUtcNow() - started, "timeout");
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, _time.GetUtcNow() - started, "unreachable");
        }
    }

    private static HttpRequestMessage BuildRequest(ProviderEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}/models", UriKind.Absolute));
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
