using System.Net.Http.Headers;

namespace Bazaar.Server.Gateway.Adapters;

/// <summary>
/// Where and how to call one provider. The gateway resolves the secret before building this; an adapter never reads secrets itself.
/// <c>ModelId</c> is the upstream model name to send. <c>OnResponseHeaders</c>, when set, sees the upstream response
/// headers of this one call before the body is read, so a provider can read its routing headers (FreeLLMAPI does).
/// </summary>
public sealed record ProviderEndpoint(
    string BaseUrl,
    string ModelId,
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    Action<HttpResponseHeaders>? OnResponseHeaders = null);
