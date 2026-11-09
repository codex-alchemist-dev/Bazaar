namespace Bazaar.Conduit;

/// <summary>
/// Source of one reachability path: it discovers or obtains a way in (an address, a tunnel, a mapped port).
/// OS, network and third-party dependencies live here, and tests fake this interface. Doc 39 section 1.
/// </summary>
public interface IConduitProvider
{
    /// <summary>Stable id, for example <c>cloudflared</c>.</summary>
    string Id { get; }

    /// <summary>Reach of the path this provider opens.</summary>
    ConduitScope Scope { get; }

    /// <summary>Checks the path without leaving anything running. Sockets it opens are closed before return.</summary>
    Task<ConduitProbeResult> ProbeAsync(ConduitContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the path and keeps it alive until the returned handle is disposed. Disposing stops child processes,
    /// removes mappings and closes sockets, with a bounded timeout.
    /// </summary>
    Task<IAsyncDisposable> OpenAsync(ConduitContext context, CancellationToken cancellationToken);
}
