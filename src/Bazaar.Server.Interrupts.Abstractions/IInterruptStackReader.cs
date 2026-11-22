namespace Bazaar.Server.Interrupts;

/// <summary>
/// Read-only seam for views that show every agent's interrupt stack at once (the Interrupts pane, doc 24 section 5, read
/// routes). Writes stay on <see cref="IInterruptStackService"/>.
/// </summary>
public interface IInterruptStackReader
{
    /// <summary>
    /// The live frames (active and suspended) of every agent that has any, grouped by agent in no fixed agent order, each agent's
    /// frames top first. Reasons are opened (decrypted) for the caller, so the caller must not log them.
    /// </summary>
    Task<IReadOnlyList<InterruptFrame>> ListLiveFramesAsync(CancellationToken ct = default);
}
