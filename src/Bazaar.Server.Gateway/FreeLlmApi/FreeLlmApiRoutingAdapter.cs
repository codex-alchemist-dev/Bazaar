using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// The routing headers FreeLLMAPI sent on the most recent call, plus call counters. In memory only: a restart clears it.
/// Holds model ids and platform names, never the key.
/// </summary>
public sealed class FreeLlmApiRoutingDiagnostics
{
    private readonly Lock _gate = new();
    private FreeLlmApiRouting? _latest;
    private long _calls;
    private long _fallbackCalls;
    private long _malformedTrails;

    /// <summary>The routing read from the last response, or null before the first call.</summary>
    public FreeLlmApiRouting? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Calls whose response carried routing headers.</summary>
    public long Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>Calls where FreeLLMAPI tried at least one other hop first.</summary>
    public long FallbackCalls
    {
        get
        {
            lock (_gate)
            {
                return _fallbackCalls;
            }
        }
    }

    /// <summary>Calls whose <c>X-Fallback-Trail</c> could not be read in full.</summary>
    public long MalformedTrails
    {
        get
        {
            lock (_gate)
            {
                return _malformedTrails;
            }
        }
    }

    public void Record(FreeLlmApiRouting routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        lock (_gate)
        {
            _latest = routing;
            _calls++;
            if (routing.Trail.Count > 0)
            {
                _fallbackCalls++;
            }

            if (routing.TrailMalformed)
            {
                _malformedTrails++;
            }
        }
    }
}

/// <summary>
/// Wraps the openai_chat adapter. Calls to the configured FreeLLMAPI base URL go through a dedicated adapter that carries the
/// FreeLLMAPI timeout and reads the routing headers, which are recorded in <see cref="FreeLlmApiRoutingDiagnostics"/>. Every
/// other base URL passes through to the standard adapter untouched.
/// </summary>
public sealed class FreeLlmApiRoutingAdapter(
    IModelAdapter standard,
    IModelAdapter freeLlmApi,
    string freeLlmApiBaseUrl,
    FreeLlmApiRoutingDiagnostics diagnostics) : IModelAdapter
{
    private readonly IModelAdapter _standard = standard ?? throw new ArgumentNullException(nameof(standard));
    private readonly IModelAdapter _freeLlmApi = freeLlmApi ?? throw new ArgumentNullException(nameof(freeLlmApi));
    private readonly string _baseUrl = Normalize(freeLlmApiBaseUrl);
    private readonly FreeLlmApiRoutingDiagnostics _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    public AdapterKind Kind => AdapterKind.OpenAiChat;

    public IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.Equals(Normalize(endpoint.BaseUrl), _baseUrl, StringComparison.OrdinalIgnoreCase))
        {
            return _standard.StreamAsync(endpoint, request, ct);
        }

        var previous = endpoint.OnResponseHeaders;
        var observed = endpoint with
        {
            OnResponseHeaders = headers =>
            {
                previous?.Invoke(headers);
                _diagnostics.Record(FreeLlmApiRouting.Parse(headers));
            },
        };
        return _freeLlmApi.StreamAsync(observed, request, ct);
    }

    private static string Normalize(string baseUrl) => (baseUrl ?? "").TrimEnd('/');
}
