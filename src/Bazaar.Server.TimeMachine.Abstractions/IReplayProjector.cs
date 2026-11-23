using Bazaar.Contracts.Gc;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Rebuilds a GC as it was at a seq (docs/design/26, section 4). A pure function of the log: no model call, no write. The viewer
/// filter of doc 01 applies to every replayed entry.
/// </summary>
public interface IReplayProjector
{
    /// <summary>
    /// Renders the cursor for <paramref name="viewer"/>. A seq above the viewer's grant is clamped to the grant. Throws
    /// <c>GcAccessDeniedException</c> when the viewer may not read the GC.
    /// </summary>
    Task<ReplayFrame> ProjectAsync(ReplayCursor cursor, GcPrincipal viewer, CancellationToken ct);
}
