namespace Bazaar.Server.Constellation.Lod;

/// <summary>A cluster summary: one glyph per Louvain community, at the centroid of its members (doc 19, Zoom tiers).</summary>
public sealed record ClusterGlyph(int Cluster, float[] Centroid, int Count);

/// <summary>Edges between two clusters, weighted by how many graph edges cross them. Intra-cluster edges are dropped.</summary>
public sealed record ClusterEdge(int A, int B, int Weight);

/// <summary>Node indices inside one cell of the tile grid. Cells are row-major, <see cref="Col"/> and <see cref="Row"/> start at 0.</summary>
public sealed record LodTile(int Col, int Row, int[] NodeIndices);

/// <summary>One feed the renderer draws. When <see cref="ClustersOnly"/> is true, <see cref="Tiles"/> is empty.</summary>
public sealed record LodSnapshot(
    bool ClustersOnly,
    IReadOnlyList<ClusterGlyph> Clusters,
    IReadOnlyList<ClusterEdge> ClusterEdges,
    IReadOnlyList<LodTile> Tiles);

/// <summary>
/// Builds the tile and level-of-detail feed from a layout (doc 19: Far, Mid and Near tiers; <c>constellation.node_cap</c>).
/// Positions are packed with a stride of <c>dimensions</c> (2 or 3), the same layout the layout core emits. Tiles bin on
/// x and y only, so 3D positions still tile in their plane. Output order is deterministic for the same input.
/// </summary>
// TODO(component): becomes part of the Constellation Bazaar Component once the component model lands.
public static class LodFeed
{
    /// <summary>Upper bound on tile grid cells per axis.</summary>
    public const int MaxTileGridSize = 256;

    /// <summary>Builds the feed for one graph version.</summary>
    /// <param name="positions">Packed coordinates, stride <paramref name="dimensions"/>. Every value must be finite.</param>
    /// <param name="dimensions">2 or 3.</param>
    /// <param name="clusterOf">Cluster index per node, same order as the positions.</param>
    /// <param name="edges">Graph edges as node-index pairs with a weight.</param>
    /// <param name="nodeCap">Above this node count the feed is clusters only (<c>constellation.node_cap</c>).</param>
    /// <param name="tileGridSize">Cells per axis for the tile grid, 1 to <see cref="MaxTileGridSize"/>.</param>
    public static LodSnapshot Build(
        ReadOnlySpan<float> positions,
        int dimensions,
        ReadOnlySpan<int> clusterOf,
        ReadOnlySpan<(int From, int To, int Weight)> edges,
        int nodeCap,
        int tileGridSize)
    {
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        }

        if (positions.Length % dimensions != 0)
        {
            throw new ArgumentException($"Positions length must be a multiple of {dimensions}.", nameof(positions));
        }

        if (nodeCap < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeCap), nodeCap, "Node cap must be at least 1.");
        }

        if (tileGridSize is < 1 or > MaxTileGridSize)
        {
            throw new ArgumentOutOfRangeException(nameof(tileGridSize), tileGridSize, $"Tile grid size must be 1 to {MaxTileGridSize}.");
        }

        var nodeCount = positions.Length / dimensions;
        if (clusterOf.Length != nodeCount)
        {
            throw new ArgumentException("Cluster assignments must have one entry per node.", nameof(clusterOf));
        }

        for (var i = 0; i < positions.Length; i++)
        {
            if (!float.IsFinite(positions[i]))
            {
                throw new ArgumentException("Positions must be finite.", nameof(positions));
            }
        }

        foreach (var (from, to, weight) in edges)
        {
            if ((uint)from >= (uint)nodeCount || (uint)to >= (uint)nodeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(edges), (from, to), "Edge endpoint is outside the node range.");
            }

            if (weight < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(edges), weight, "Edge weight must not be negative.");
            }
        }

        var clusters = BuildClusters(positions, dimensions, clusterOf);
        var clusterEdges = BuildClusterEdges(edges, clusterOf);
        var clustersOnly = nodeCount > nodeCap;
        var tiles = clustersOnly
            ? []
            : BuildTiles(positions, dimensions, nodeCount, tileGridSize);

        return new LodSnapshot(clustersOnly, clusters, clusterEdges, tiles);
    }

    private static List<ClusterGlyph> BuildClusters(ReadOnlySpan<float> positions, int dimensions, ReadOnlySpan<int> clusterOf)
    {
        var sums = new SortedDictionary<int, (double[] Sum, int Count)>();
        for (var node = 0; node < clusterOf.Length; node++)
        {
            var cluster = clusterOf[node];
            if (!sums.TryGetValue(cluster, out var entry))
            {
                entry = (new double[dimensions], 0);
            }

            for (var d = 0; d < dimensions; d++)
            {
                entry.Sum[d] += positions[(node * dimensions) + d];
            }

            sums[cluster] = (entry.Sum, entry.Count + 1);
        }

        var glyphs = new List<ClusterGlyph>(sums.Count);
        foreach (var (cluster, (sum, count)) in sums)
        {
            var centroid = new float[dimensions];
            for (var d = 0; d < dimensions; d++)
            {
                centroid[d] = (float)(sum[d] / count);
            }

            glyphs.Add(new ClusterGlyph(cluster, centroid, count));
        }

        return glyphs;
    }

    private static List<ClusterEdge> BuildClusterEdges(ReadOnlySpan<(int From, int To, int Weight)> edges, ReadOnlySpan<int> clusterOf)
    {
        var weights = new SortedDictionary<(int A, int B), int>();
        foreach (var (from, to, weight) in edges)
        {
            var a = clusterOf[from];
            var b = clusterOf[to];
            if (a == b || weight == 0)
            {
                continue;
            }

            var key = a < b ? (a, b) : (b, a);
            weights[key] = weights.GetValueOrDefault(key) + weight;
        }

        return [.. weights.Select(pair => new ClusterEdge(pair.Key.A, pair.Key.B, pair.Value))];
    }

    private static List<LodTile> BuildTiles(ReadOnlySpan<float> positions, int dimensions, int nodeCount, int tileGridSize)
    {
        if (nodeCount == 0)
        {
            return [];
        }

        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        for (var node = 0; node < nodeCount; node++)
        {
            var x = positions[node * dimensions];
            var y = positions[(node * dimensions) + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var spanX = Math.Max(maxX - minX, float.Epsilon);
        var spanY = Math.Max(maxY - minY, float.Epsilon);

        var cells = new SortedDictionary<int, List<int>>();
        for (var node = 0; node < nodeCount; node++)
        {
            var col = CellIndex(positions[node * dimensions] - minX, spanX, tileGridSize);
            var row = CellIndex(positions[(node * dimensions) + 1] - minY, spanY, tileGridSize);
            var key = (row * tileGridSize) + col;
            if (!cells.TryGetValue(key, out var members))
            {
                members = [];
                cells[key] = members;
            }

            members.Add(node);
        }

        var tiles = new List<LodTile>(cells.Count);
        foreach (var (key, members) in cells)
        {
            tiles.Add(new LodTile(key % tileGridSize, key / tileGridSize, [.. members]));
        }

        return tiles;
    }

    private static int CellIndex(float offset, float span, int tileGridSize)
    {
        var index = (int)(offset / span * tileGridSize);
        return Math.Clamp(index, 0, tileGridSize - 1);
    }
}
