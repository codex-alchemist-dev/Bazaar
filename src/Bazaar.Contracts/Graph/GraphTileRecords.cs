namespace Bazaar.Contracts.Graph;

/// <summary>
/// Zoom tier a client asks for (doc 19, zoom tiers). <see cref="Far"/> draws cluster glyphs and cluster-to-cluster edges,
/// <see cref="Mid"/> draws node shapes and type colour, and <see cref="Near"/> draws all labels, edge kinds and hover previews.
/// The values are ordered from coarsest to finest.
/// </summary>
public enum ZoomTier
{
    Far = 0,
    Mid = 1,
    Near = 2,
}

/// <summary>
/// The address of one spatial tile. <c>Level</c> is the subdivision depth (quadtree in 2D, octree-ready for 3D), and
/// <c>X</c>, <c>Y</c>, <c>Z</c> are the cell index at that level. A 2D view uses <c>Z = 0</c>.
/// </summary>
public readonly record struct TileKey
{
    /// <summary>Rejects a negative level. Cell indices may be any value, because the grid is unbounded.</summary>
    public TileKey(int level, int x, int y, int z = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        Level = level;
        X = x;
        Y = y;
        Z = z;
    }

    public int Level { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Z { get; init; }
}

/// <summary>
/// A client's request for the nodes inside a box, at one zoom tier (doc 19). Bounds are in layout space; a 2D view leaves
/// the Z bounds at 0. <c>MaxNodes</c> caps the frame, so a box over a huge graph returns clusters instead of every node.
/// </summary>
public sealed record ViewportRequest
{
    /// <summary>Rejects an inverted box and a non-positive node cap.</summary>
    public ViewportRequest(
        string viewId,
        long graphVersion,
        ZoomTier tier,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        int maxNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxNodes);
        if (minX > maxX || minY > maxY || minZ > maxZ)
            throw new ArgumentException("The box minimum must not exceed its maximum on any axis.");

        ViewId = viewId;
        GraphVersion = graphVersion;
        Tier = tier;
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
        MaxNodes = maxNodes;
    }

    public string ViewId { get; init; }

    public long GraphVersion { get; init; }

    public ZoomTier Tier { get; init; }

    public float MinX { get; init; }

    public float MinY { get; init; }

    public float MinZ { get; init; }

    public float MaxX { get; init; }

    public float MaxY { get; init; }

    public float MaxZ { get; init; }

    public int MaxNodes { get; init; }
}

/// <summary>
/// A stand-in for a cluster in the far tier: one glyph at the cluster's centroid, with how many nodes it holds.
/// </summary>
public sealed record ClusterGlyph
{
    /// <summary>Rejects a negative member count.</summary>
    public ClusterGlyph(string clusterId, int memberCount, float x, float y, float z)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memberCount);
        ClusterId = clusterId;
        MemberCount = memberCount;
        X = x;
        Y = y;
        Z = z;
    }

    public string ClusterId { get; init; }

    public int MemberCount { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }
}

/// <summary>
/// One tile of layout positions sent to a client. <c>Xyz</c> is packed x,y,z per node, in the order of <c>NodeIds</c>, with
/// stride 3, the same as <see cref="LayoutFrame"/>. <c>Dimensions</c> is 2 or 3: a 2D view ignores z, which the server sets to
/// 0. Clients drop any tile older than the newest one applied for the same key.
/// </summary>
public sealed record TileFrame
{
    /// <summary>Rejects a dimension other than 2 or 3, and a position buffer that does not hold three floats per node.</summary>
    public TileFrame(
        string viewId,
        long graphVersion,
        long layoutSeq,
        TileKey key,
        ZoomTier tier,
        byte dimensions,
        IReadOnlyList<string> nodeIds,
        ReadOnlyMemory<float> xyz,
        IReadOnlyList<ClusterGlyph> clusters)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(clusters);
        if (dimensions is not (2 or 3))
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be 2 or 3.");
        if (xyz.Length != nodeIds.Count * 3)
            throw new ArgumentException("Xyz must hold three floats for each node id.", nameof(xyz));

        ViewId = viewId;
        GraphVersion = graphVersion;
        LayoutSeq = layoutSeq;
        Key = key;
        Tier = tier;
        Dimensions = dimensions;
        NodeIds = nodeIds;
        Xyz = xyz;
        Clusters = clusters;
    }

    public string ViewId { get; init; }

    public long GraphVersion { get; init; }

    public long LayoutSeq { get; init; }

    public TileKey Key { get; init; }

    public ZoomTier Tier { get; init; }

    public byte Dimensions { get; init; }

    public IReadOnlyList<string> NodeIds { get; init; }

    public ReadOnlyMemory<float> Xyz { get; init; }

    public IReadOnlyList<ClusterGlyph> Clusters { get; init; }
}
