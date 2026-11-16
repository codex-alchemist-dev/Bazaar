namespace Bazaar.Client.Core.Server;

/// <summary>Where the client reaches its Bazaar.Server, and the bearer credential it sends with each HTTP request.</summary>
public sealed record ServerEndpoint(Uri BaseAddress, string? Credential = null)
{
    /// <summary>
    /// Local debug default: a server on this machine over plaintext loopback, with no credential. Release builds never use it;
    /// they connect to the server they were built for (<see cref="ClientServer.ConnectToCompanyServer"/>).
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

    /// <summary>
    /// Points the client at the server this build was made for (doc 37 section 4a). A debug build with no identity keeps
    /// <see cref="ServerEndpoint.Development"/>. A release build with no identity throws, so it fails closed instead of guessing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A release build carries no company server identity.</exception>
    public static void ConnectToCompanyServer()
    {
        if (CompanyBuildIdentity.Address is { } address)
        {
            Use(new ServerEndpoint(address));
            return;
        }

#if !DEBUG
        throw new InvalidOperationException(
            "This client build has no company server identity. Build it from its company server (doc 37 section 4a).");
#endif
    }
}
