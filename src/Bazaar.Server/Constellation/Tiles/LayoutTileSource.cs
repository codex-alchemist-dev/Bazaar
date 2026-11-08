using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Layout;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// The real tile source: it lays out the graph on demand (doc 19). The graph comes from <see cref="IConstellationGraphSource"/>.
/// The layout is deterministic (fixed seed), so the same graph version always gives the same positions. The settled layout is
/// cached for one graph version; a new version replaces it. A frame holds the nodes inside the request box, in layout order, up
/// to <see cref="ViewportRequest.MaxNodes"/>.
/// </summary>
public sealed class LayoutTileSource(IConstellationGraphSource graphs) : IConstellationTileSource
{
    /// <summary>Largest per-tick movement that counts as settled.</summary>
    private const double _settleTolerance = 0.01;

    /// <summary>Upper bound on layout ticks, so one graph cannot hold a request for long.</summary>
    private const int _maxSettleTicks = 500;

    private readonly IConstellationGraphSource _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
    private readonly object _gate = new();
    private CachedLayout? _cached;

    private sealed record CachedLayout(long GraphVersion, IReadOnlyList<string> NodeIds, float[] Xyz);

    public async Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await _graphs.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var layout = LayoutFor(snapshot);

        var ids = new List<string>();
        var picked = new List<float>();
        for (var i = 0; i < layout.NodeIds.Count && ids.Count < request.MaxNodes; i++)
        {
            var x = layout.Xyz[3 * i];
            var y = layout.Xyz[(3 * i) + 1];
            var z = layout.Xyz[(3 * i) + 2];
            if (x < request.MinX || x > request.MaxX || y < request.MinY || y > request.MaxY || z < request.MinZ || z > request.MaxZ)
                continue;

            ids.Add(layout.NodeIds[i]);
            picked.Add(x);
            picked.Add(y);
            picked.Add(z);
        }

        return new TileFrame(
            request.ViewId,
            layout.GraphVersion,
            layoutSeq: 1,
            new TileKey(0, 0, 0),
            request.Tier,
            dimensions: 2,
            ids,
            picked.ToArray(),
            Array.Empty<ClusterGlyph>());
    }

    private CachedLayout LayoutFor(GraphSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_cached is { } cached && cached.GraphVersion == snapshot.GraphVersion)
                return cached;

            var graph = LayoutGraph.FromSnapshot(snapshot);
            var positions = Array.Empty<float>();
            if (graph.NodeCount > 0)
            {
                var layout = new ForceLayout(graph, new LayoutOptions { Seed = 1, Dimensions = 2 });
                layout.RunUntilSettled(_settleTolerance, _maxSettleTicks);
                positions = layout.GetPositions();
            }

            _cached = new CachedLayout(snapshot.GraphVersion, graph.NodeIds, positions);
            return _cached;
        }
    }
}
