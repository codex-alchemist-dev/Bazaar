using Bazaar.Contracts.Graph;

// Tile layout follows constellation-scale-and-lod.md section 3.

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>Settings for <see cref="QuadtreeTileBuilder"/>. Tiles are built for every level from 0 to <see cref="MaxDepth"/>.</summary>
public sealed record QuadtreeTileOptions
{
    /// <summary>
    /// Deepest subdivision level built. Level 0 is one tile over the whole layout, and each level below splits every
    /// cell into four. The default is a starting value under the configurability rule (doc 19 scale section).
    /// </summary>
    public int MaxDepth { get; init; } = 4;
}

/// <summary>
/// Builds quadtree tiles over a settled layout (constellation scale doc, section 3). The layout is a set of node ids and
/// packed x,y,z positions, in the same order as <see cref="Layout.ForceLayout.GetPositions"/>. The bounding square of the layout
/// is split into a grid of 2^level cells per side, and every node is placed in one cell at each level.
/// </summary>
/// <remarks>
/// Each cell index is the floor of the node's scaled position, so a child cell always lies inside its parent cell. Output
/// is sorted by level, then X, then Y, and nodes keep their input order inside a tile, so the same input gives the same
/// output. Only non-empty tiles are returned. The 2D view sets Z to 0. Cluster glyphs are empty, because the Louvain
/// clustering is not in this builder, and the scale doc's per-tile counts, bounding boxes and quantised records are not
/// part of <see cref="TileFrame"/> yet.
/// </remarks>
public static class QuadtreeTileBuilder
{
    /// <summary>Deepest level accepted. Cell indices at this depth still fit in an int.</summary>
    public const int MaxSupportedDepth = 30;

    /// <summary>Builds the tiles for a settled layout. An empty layout gives no tiles.</summary>
    /// <param name="viewId">The view the tiles are for.</param>
    /// <param name="graphVersion">The graph version the layout was computed from.</param>
    /// <param name="layoutSeq">The layout sequence number, carried into every tile.</param>
    /// <param name="tier">The zoom tier the tiles are for.</param>
    /// <param name="nodeIds">Node ids in layout order.</param>
    /// <param name="xyz">Packed x,y,z positions, stride 3, one triple per node in <paramref name="nodeIds"/> order.</param>
    /// <param name="options">Tile settings. Defaults when null.</param>
    public static IReadOnlyList<TileFrame> Build(
        string viewId,
        long graphVersion,
        long layoutSeq,
        ZoomTier tier,
        IReadOnlyList<string> nodeIds,
        ReadOnlyMemory<float> xyz,
        QuadtreeTileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(viewId);
        ArgumentNullException.ThrowIfNull(nodeIds);
        options ??= new QuadtreeTileOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxDepth, MaxSupportedDepth);

        if (xyz.Length != nodeIds.Count * 3)
            throw new ArgumentException("Xyz must hold three floats for each node id.", nameof(xyz));
        if (nodeIds.Count == 0)
            return Array.Empty<TileFrame>();

        var values = xyz.Span;
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        for (var i = 0; i < nodeIds.Count; i++)
        {
            var x = values[3 * i];
            var y = values[(3 * i) + 1];
            if (!float.IsFinite(x) || !float.IsFinite(y))
                throw new ArgumentException("Node positions must be finite.", nameof(xyz));

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        // The grid is a square over the larger span, so cells stay square. A layout with one point has no span; every node
        // then falls in cell 0 at each level.
        var side = Math.Max(maxX - minX, maxY - minY);
        if (side <= 0)
            side = 1;

        // Fractions are in [0, 1]. Scaling by a power of two is exact, so the cell index at level L+1 is always twice the
        // cell index at level L, plus 0 or 1. That is what keeps each child inside its parent.
        var fractionX = new double[nodeIds.Count];
        var fractionY = new double[nodeIds.Count];
        for (var i = 0; i < nodeIds.Count; i++)
        {
            fractionX[i] = (values[3 * i] - minX) / side;
            fractionY[i] = (values[(3 * i) + 1] - minY) / side;
        }

        var tiles = new List<TileFrame>();
        for (var level = 0; level <= options.MaxDepth; level++)
        {
            var cellsPerSide = 1 << level;
            var members = new SortedDictionary<(int X, int Y), List<int>>();
            for (var i = 0; i < nodeIds.Count; i++)
            {
                var cellX = CellIndex(fractionX[i], level, cellsPerSide);
                var cellY = CellIndex(fractionY[i], level, cellsPerSide);
                if (!members.TryGetValue((cellX, cellY), out var list))
                {
                    list = new List<int>();
                    members[(cellX, cellY)] = list;
                }

                list.Add(i);
            }

            foreach (var (cell, indices) in members)
            {
                tiles.Add(BuildTile(viewId, graphVersion, layoutSeq, tier, level, cell.X, cell.Y, nodeIds, values, indices));
            }
        }

        return tiles;
    }

    private static int CellIndex(double fraction, int level, int cellsPerSide)
    {
        var cell = (int)Math.Floor(Math.ScaleB(fraction, level));
        return Math.Clamp(cell, 0, cellsPerSide - 1);
    }

    private static TileFrame BuildTile(
        string viewId,
        long graphVersion,
        long layoutSeq,
        ZoomTier tier,
        int level,
        int cellX,
        int cellY,
        IReadOnlyList<string> nodeIds,
        ReadOnlySpan<float> values,
        List<int> indices)
    {
        var ids = new string[indices.Count];
        var packed = new float[indices.Count * 3];
        for (var k = 0; k < indices.Count; k++)
        {
            var i = indices[k];
            ids[k] = nodeIds[i];
            packed[3 * k] = values[3 * i];
            packed[(3 * k) + 1] = values[(3 * i) + 1];
            packed[(3 * k) + 2] = 0;
        }

        return new TileFrame(
            viewId,
            graphVersion,
            layoutSeq,
            new TileKey(level, cellX, cellY, 0),
            tier,
            dimensions: 2,
            ids,
            packed,
            Array.Empty<ClusterGlyph>());
    }
}
