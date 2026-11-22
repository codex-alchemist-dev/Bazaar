using Bazaar.Contracts.Graph;

namespace Bazaar.Client.Constellation.Abstractions;

/// <summary>
/// Fetches the tile for one viewport request from the server (the Constellation tile endpoint, <c>POST /constellation/tiles</c>).
/// The pane depends on this interface only, so a head injects the HTTP client and tests inject a fake. Returns null when the
/// server has nothing to show for the box.
/// </summary>
public interface IConstellationTileSource
{
    Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default);
}
