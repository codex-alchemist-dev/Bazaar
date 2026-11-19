using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Supplies the tile for a viewport request (doc 19, tile and LOD feed). The real source reads the layout store; tests and
/// the first endpoint wiring use <c>FakeConstellationTileSource</c>. Returns null when there is nothing to show.
/// </summary>
public interface IConstellationTileSource
{
    Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tile one viewer may see (doc 19, "Visibility"): nodes, edges, positions and the graph version come from that viewer's
    /// projection only. The default fails closed: the owner gets the plain tile and everyone else gets none.
    /// </summary>
    Task<TileFrame?> GetFrameAsync(ViewportRequest request, GraphViewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.IsOwner ? GetFrameAsync(request, cancellationToken) : Task.FromResult<TileFrame?>(null);
    }
}
