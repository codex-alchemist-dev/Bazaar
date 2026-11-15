using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Supplies the tile for a viewport request (doc 19, tile and LOD feed). The real source reads the layout store; tests and
/// the first endpoint wiring use <c>FakeConstellationTileSource</c>. Returns null when there is nothing to show.
/// </summary>
public interface IConstellationTileSource
{
    Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default);
}
