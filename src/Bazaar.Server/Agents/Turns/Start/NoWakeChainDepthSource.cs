namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>The default <see cref="IWakeChainDepthSource"/>: it never reads a depth. Replaced by a host that owns a wake kind.</summary>
public sealed class NoWakeChainDepthSource : IWakeChainDepthSource
{
    public Task<int?> ChainDepthAsync(string inboxKind, string? sourceRef, CancellationToken ct) => Task.FromResult<int?>(null);
}
