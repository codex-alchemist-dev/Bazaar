using Bazaar.CompanyIdentity;

namespace Bazaar.Client.Core.Server;

/// <summary>
/// Where the client reaches its Bazaar.Server, the bearer credential it sends with each HTTP request, and the key-hash pin of
/// the server (<see cref="ServerPin"/>). An https address needs a pin; the Development endpoint is plaintext loopback and has none.
/// </summary>
public sealed record ServerEndpoint(Uri BaseAddress, string? Credential = null, string? KeyHashPin = null)
{
    /// <summary>
    /// Local debug default: a server on this machine over plaintext loopback, with no credential. Release builds never use it;
    /// they connect to the server they were built for (<see cref="ClientServer.ConnectToCompanyServer"/>).
    /// </summary>
    public static ServerEndpoint Development { get; } = new(new Uri("http://127.0.0.1:8080/"));

    /// <summary>
    /// Creates an HTTP client for this endpoint. An https endpoint gets a pinned handler. Fails closed: an https endpoint with no
    /// pin, or a plaintext endpoint that is not on loopback, is refused.
    /// </summary>
    /// <exception cref="InvalidOperationException">The endpoint is not safe to connect to.</exception>
    public HttpClient CreateHttpClient()
    {
        var http = new HttpClient(CreateHandler()) { BaseAddress = BaseAddress };
        return http;
    }

    /// <summary>The handler for this endpoint: a pinned handler for https, or the platform default for loopback http.</summary>
    /// <exception cref="InvalidOperationException">The endpoint is not safe to connect to.</exception>
    public HttpMessageHandler CreateHandler()
    {
        if (string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (!ServerPin.IsWellFormed(KeyHashPin))
                throw new InvalidOperationException("This server address is https but the build carries no valid key-hash pin. Refusing to connect.");
            return ServerPin.CreatePinnedHandler(KeyHashPin!);
        }

        if (string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && BaseAddress.IsLoopback)
            return new HttpClientHandler();

        throw new InvalidOperationException("Plain http is allowed only on loopback. Refusing to connect.");
    }
}

/// <summary>
/// The endpoint this client process talks to. A head sets it once at startup; panes built afterwards use it. It
/// is shared by every head, so the Windows and Android shells configure the same thing.
/// </summary>
public static class ClientServer
{
    private static ServerEndpoint _current = ServerEndpoint.Development;

    /// <summary>The endpoint in effect now. Starts at <see cref="ServerEndpoint.Development"/>.</summary>
    public static ServerEndpoint Current => Volatile.Read(ref _current);

    /// <summary>Replaces the endpoint. Clients built before the call keep the endpoint they were given.</summary>
    public static void Use(ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Volatile.Write(ref _current, endpoint);
    }

    /// <summary>
    /// Points the client at the server this build was made for (doc 37 section 4a). A debug build with no identity keeps
    /// <see cref="ServerEndpoint.Development"/>. A release build with no identity throws, so it fails closed instead of guessing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A release build carries no company server identity.</exception>
    public static void ConnectToCompanyServer()
    {
        if (CompanyBuildIdentity.Address is { } address)
        {
            // The pin is part of the identity. Without a valid one, the build cannot prove it reached its own server, so refuse
            // in every configuration, debug included.
            var pin = CompanyBuildIdentity.KeyHashPin;
            if (!ServerPin.IsWellFormed(pin))
                throw new InvalidOperationException("This client build has a server address but no valid key-hash pin. Refusing to connect.");
            var endpoint = new ServerEndpoint(address, KeyHashPin: pin);
            _ = endpoint.CreateHandler(); // throws when the address is not https or loopback http
            Use(endpoint);
            return;
        }

#if !DEBUG
        throw new InvalidOperationException(
            "This client build has no company server identity. Build it from its company server (doc 37 section 4a).");
#endif
    }
}
