using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Supplies the level-of-detail feed for a graph on <c>graph:&lt;id&gt;</c>. The source also decides access: it returns null
/// when the principal may not read the graph or the graph does not exist, so the socket answers <c>denied</c> for both
/// (membership does not leak). The returned frame's <see cref="GraphLodFrame.S"/> may be empty; the engine fills it in.
/// </summary>
public interface IGraphLodSource
{
    ValueTask<GraphLodFrame?> GetAsync(Guid principalId, Guid graphId, CancellationToken ct);
}
