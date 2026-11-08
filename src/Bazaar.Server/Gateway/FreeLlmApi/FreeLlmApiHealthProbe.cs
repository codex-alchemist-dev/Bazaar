using System.Net.Http.Headers;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Health;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Probes a FreeLLMAPI endpoint with <c>GET {BaseUrl}/models?available=true</c>. Healthy means a 2xx answer whose
/// model list holds at least one row that is ready (or has no execution status). The injected <see cref="HttpClient"/>
/// must have its <see cref="HttpClient.Timeout"/> set. Upstream problems never throw; the detail is fixed-form and
/// never carries the key, a header value or an upstream body.
/// </summary>
public sealed class FreeLlmApiHealthProbe(HttpClient http, TimeProvider? time = null) : IHealthProbe
{
    private const string _modelsPath = "/models?available=true";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<ProbeResult> ProbeAsync(ProviderEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var request = BuildRequest(endpoint);
        var started = _time.GetUtcNow();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(false, _time.GetUtcNow() - started, $"HTTP {status}");
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var latency = _time.GetUtcNow() - started;
            if (!FreeLlmApiModelCatalog.TryParse(body, out var models))
            {
                return new ProbeResult(false, latency, "malformed model list");
            }

            var ready = 0;
            foreach (var model in models)
            {
                if (model.ExecutionStatus is null || model.ExecutionStatus == FreeLlmApiModel.ReadyStatus)
                {
                    ready++;
                }
            }

            return ready == 0
                ? new ProbeResult(false, latency, "no ready models")
                : new ProbeResult(true, latency, $"HTTP {status}, {ready} ready");
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
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{baseUrl}{_modelsPath}", UriKind.Absolute));
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
