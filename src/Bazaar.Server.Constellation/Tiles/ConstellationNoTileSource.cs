using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// The tile source the Constellation component registers by default. It has no layout store behind it yet, so it returns no frame
/// and the handler answers 404. A layout-store component replaces it with a real source through <c>TryAdd</c> ordering, or by
/// registering its own <see cref="IConstellationTileSource"/> first.
/// </summary>
public sealed class ConstellationNoTileSource : IConstellationTileSource
{
    public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<TileFrame?>(null);
    }
}
