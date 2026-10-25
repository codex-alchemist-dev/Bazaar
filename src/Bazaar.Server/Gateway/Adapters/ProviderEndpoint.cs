namespace Bazaar.Server.Gateway.Adapters;

/// <summary>
/// Where and how to call one provider. The gateway resolves the secret before building this; an adapter never reads secrets itself.
/// <c>ModelId</c> is the upstream model name to send.
/// </summary>
public sealed record ProviderEndpoint(
    string BaseUrl,
    string ModelId,
    string? ApiKey = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null);
