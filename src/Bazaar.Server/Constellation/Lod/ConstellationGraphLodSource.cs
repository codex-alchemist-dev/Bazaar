using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Clustering;
using Bazaar.Server.Constellation.Layout;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Constellation.Lod;

/// <summary>Decides whether a principal may read one graph on the Constellation. The snapshot itself is not filtered per viewer.</summary>
public interface IConstellationAccessPolicy
{
    ValueTask<bool> CanReadAsync(Guid principalId, Guid graphId, CancellationToken cancellationToken);
}

/// <summary>
/// Serves the <c>graph:&lt;id&gt;</c> level-of-detail feed (doc 19). It is the only path from the shared graph source to the
/// socket: it checks access, reads the snapshot from <see cref="IConstellationGraphSource"/>, lays it out, clusters it and
/// builds the <see cref="LodFeed"/>. Tiles and LOD therefore read the same snapshot and version.
/// </summary>
public sealed class ConstellationGraphLodSource(
    IConstellationGraphSource graph,
    IConstellationAccessPolicy access,
    Guid graphId,
    int nodeCap,
    int tileGridSize = 64) : IGraphLodSource
{
    /// <summary>Layout stops when the largest per-tick move falls under this (fixed, so output is deterministic).</summary>
    private const double _settleTolerance = 0.01;

    /// <summary>Upper bound on layout ticks per snapshot.</summary>
    private const int _maxLayoutTicks = 500;

    private readonly IConstellationGraphSource _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    private readonly IConstellationAccessPolicy _access = access ?? throw new ArgumentNullException(nameof(access));
    private readonly Guid _graphId = graphId;
    private readonly int _nodeCap = nodeCap > 0
        ? nodeCap
        : throw new ArgumentOutOfRangeException(nameof(nodeCap), nodeCap, "The node cap must be positive.");
    private readonly int _tileGridSize = tileGridSize is >= 1 and <= LodFeed.MaxTileGridSize
        ? tileGridSize
        : throw new ArgumentOutOfRangeException(nameof(tileGridSize), tileGridSize, "The tile grid size is out of range.");

    public async ValueTask<GraphLodFrame?> GetAsync(Guid principalId, Guid graphId, CancellationToken ct)
    {
        // This source serves one graph. Any other id is denied the same way as an unreadable one.
        if (graphId != _graphId)
        {
            return null;
        }

        if (!await _access.CanReadAsync(principalId, graphId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = await _graph.GetSnapshotAsync(ct).ConfigureAwait(false);
        return Build(snapshot, _nodeCap, _tileGridSize);
    }

    /// <summary>Lays out, clusters and tiles one snapshot. The frame's <c>S</c> is empty; the socket fills it in.</summary>
    internal static GraphLodFrame Build(GraphSnapshot snapshot, int nodeCap, int tileGridSize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Nodes.Count == 0)
        {
            return new GraphLodFrame(string.Empty, snapshot.GraphVersion, false, [], [], []);
        }

        var layoutGraph = LayoutGraph.FromSnapshot(snapshot);
        var layout = new ForceLayout(layoutGraph, new LayoutOptions());
        layout.RunUntilSettled(_settleTolerance, _maxLayoutTicks);

        // Positions are packed x,y,z (stride 3). The 2D layout keeps z at 0, so the feed is read with stride 3.
        var positions = layout.GetPositions();

        var ids = layoutGraph.NodeIds;
        var weightedEdges = layoutGraph.Edges
            .Select(e => new WeightedEdge(ids[e.Source], ids[e.Target], 1.0))
            .ToArray();
        var community = LouvainClustering.Detect(ids, weightedEdges).CommunityByNode;
        var clusterOf = ids.Select(id => community[id]).ToArray();

        var edges = layoutGraph.Edges
            .Select(e => (e.Source, e.Target, 1))
            .ToArray();

        var lod = LodFeed.Build(positions, 3, clusterOf, edges, nodeCap, tileGridSize);

        return new GraphLodFrame(
            string.Empty,
            snapshot.GraphVersion,
            lod.ClustersOnly,
            lod.Clusters.Select(c => new GraphLodGlyph(c.Cluster, c.Centroid, c.Count)).ToArray(),
            lod.ClusterEdges.Select(e => new GraphLodEdge(e.A, e.B, e.Weight)).ToArray(),
            lod.Tiles.Select(t => new GraphLodTile(t.Col, t.Row, t.NodeIndices)).ToArray());
    }
}
