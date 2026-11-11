namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>
/// Reads the chain depth a woken turn runs at, from one inbox item (doc 38, Chain depth). The host supplies the
/// implementation: a component that owns a wake kind answers for its own items, and everything else answers null.
/// </summary>
public interface IWakeChainDepthSource
{
    /// <summary>
    /// The chain depth of the item, or null when this source cannot read one. Null means "no depth from this source";
    /// <see cref="TurnStarter"/> decides what a null means for each kind.
    /// </summary>
    Task<int?> ChainDepthAsync(string inboxKind, string? sourceRef, CancellationToken ct);
}
