using Bazaar.CompanyIdentity;

namespace Bazaar.Client.Abstractions.Server;

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
    /// The reason this endpoint must not be contacted, or null when it may be. An https endpoint needs a pin. Plain http is
    /// allowed only on loopback. Fails closed without throwing, so building a client never fails; each request is refused instead.
    /// </summary>
    public string? RefusalReason()
    {
        if (string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return ServerPin.IsWellFormed(KeyHashPin)
                ? null
                : "This server address is https but the build carries no valid key-hash pin. Refusing to connect.";

        if (string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && BaseAddress.IsLoopback)
            return null;

        return "Plain http is allowed only on loopback. Refusing to connect.";
    }

    /// <summary>Creates an HTTP client for this endpoint. Requests to a refused endpoint fail without touching the network.</summary>
    public HttpClient CreateHttpClient() => new(CreateHandler()) { BaseAddress = BaseAddress };

    /// <summary>
    /// The handler for this endpoint: a pinned handler for https, the platform default for loopback http, and a handler that
    /// refuses every request for anything else.
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var refusal = RefusalReason();
        if (refusal is not null) return new RefusingHandler(refusal);

        return string.Equals(BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? ServerPin.CreatePinnedHandler(KeyHashPin!)
            : new HttpClientHandler();
    }

    /// <summary>Refuses every request with the reason, before any connection is made.</summary>
    private sealed class RefusingHandler(string reason) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException(reason));
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
            var refusal = endpoint.RefusalReason();
            if (refusal is not null) throw new InvalidOperationException(refusal);
            Use(endpoint);
            return;
        }

#if !DEBUG
        throw new InvalidOperationException(
            "This client build has no company server identity. Build it from its company server (doc 37 section 4a).");
#endif
    }
}
