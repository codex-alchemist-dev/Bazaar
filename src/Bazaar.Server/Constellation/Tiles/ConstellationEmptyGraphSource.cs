using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// The graph source registered by default. No graph store is wired into the host yet, so it reports an empty graph at version 0.
/// Views get an empty frame, not a 404, until a store-backed source replaces it through <c>TryAdd</c> ordering.
/// </summary>
public sealed class ConstellationEmptyGraphSource : IConstellationGraphSource
{
    public Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphSnapshot(0, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>()));
}
