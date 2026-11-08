using Bazaar.Contracts.Graph;

namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// Fetches the tile for one viewport request from the server (the Constellation tile endpoint, <c>POST /constellation/tiles</c>).
/// The pane depends on this interface only, so a head injects the HTTP client and tests inject a fake. Returns null when the
/// server has nothing to show for the box.
/// </summary>
public interface IConstellationTileSource
{
    Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A box in graph (layout) coordinates, used as the visible area of the Constellation pane.</summary>
public readonly record struct ConstellationBox(float MinX, float MinY, float MaxX, float MaxY);

/// <summary>One node of a tile frame, positioned in graph coordinates.</summary>
public readonly record struct TileNode(string Id, float X, float Y);
