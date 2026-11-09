namespace Bazaar.Server.Transports;

/// <summary>
/// Source of one reachability path: it discovers or obtains a way in (an address, a tunnel, a mapped port).
/// OS, network and third-party dependencies live here, and tests fake this interface. Doc 39 section 1.
/// </summary>
public interface IReachabilityProvider
{
    /// <summary>Stable id, for example <c>cloudflared</c>.</summary>
    string Id { get; }

    /// <summary>Reach of the path this provider opens.</summary>
    TransportScope Scope { get; }

    /// <summary>Checks the path without leaving anything running. Sockets it opens are closed before return.</summary>
    Task<ReachabilityResult> ProbeAsync(ReachabilityContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the path and keeps it alive until the returned handle is disposed. Disposing stops child processes,
    /// removes mappings and closes sockets, with a bounded timeout.
    /// </summary>
    Task<IAsyncDisposable> OpenAsync(ReachabilityContext context, CancellationToken cancellationToken);
}
