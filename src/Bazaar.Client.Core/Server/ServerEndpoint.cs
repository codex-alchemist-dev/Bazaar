namespace Bazaar.Client.Core.Server;

/// <summary>Where the client reaches its Bazaar.Server, and the bearer credential it sends with each HTTP request.</summary>
public sealed record ServerEndpoint(Uri BaseAddress, string? Credential = null)
{
    /// <summary>
    /// Development default: a server on this machine over plaintext loopback, with no credential. Real endpoints come from
    /// pairing (doc 32, still a spike), so a head overrides this through <see cref="ClientServer.Use"/>.
    /// </summary>
    public static ServerEndpoint Development { get; } = new(new Uri("http://127.0.0.1:8080/"));
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
}
