using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.Vault;

/// <summary>Builds <see cref="VaultApi"/> clients for an endpoint. Panes ask the factory rather than building HTTP themselves.</summary>
public static class VaultApiFactory
{
    /// <summary>
    /// Creates a client bound to <paramref name="endpoint"/>: its base address, and its credential on every request.
    /// <paramref name="handler"/> is for tests; the client owns it. A re-pair creates a new client with the new endpoint.
    /// </summary>
    public static VaultApi Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new VaultApi(http, () => endpoint.Credential);
    }
}
