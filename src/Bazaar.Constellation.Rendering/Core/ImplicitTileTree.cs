namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// A synthetic, uniformly filled quadtree or octree of any node count (billions are fine, nothing is stored).
/// Used by the selector tests and the billion-node benchmarks, so frame cost can be measured with no data behind it.
/// </summary>
public sealed class ImplicitTileTree : ITileTree
{
    private readonly TileBounds _root;
    private readonly int _branch;
    private readonly long _totalNodes;
    private readonly int _sampleSize;

    public ImplicitTileTree(TileBounds root, int dimensions, long totalNodes, int leafCapacity = 4096, int sampleSize = 256)
    {
        if (dimensions is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leafCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleSize);
        _root = root;
        Dimensions = dimensions;
        _branch = dimensions == 2 ? 4 : 8;
        _totalNodes = totalNodes;
        _sampleSize = sampleSize;
        int depth = 0;
        double perTile = totalNodes;
        while (perTile > leafCapacity && depth < 30)
        {
            perTile /= _branch;
            depth++;
        }

        LeafLevel = depth;
    }

    public int Dimensions { get; }

    public int LeafLevel { get; }

    public TileInfo GetInfo(in TileKey key)
    {
        double tiles = Math.Pow(_branch, key.Level);
        long count = (long)Math.Ceiling(_totalNodes / tiles);
        float scale = 1f / (1L << key.Level);
        float w = (_root.MaxX - _root.MinX) * scale;
        float h = (_root.MaxY - _root.MinY) * scale;
        float d = Dimensions == 3 ? (_root.MaxZ - _root.MinZ) * scale : 0f;
        float x0 = _root.MinX + (key.X * w);
        float y0 = _root.MinY + (key.Y * h);
        float z0 = Dimensions == 3 ? _root.MinZ + (key.Z * d) : 0f;
        bool leaf = key.Level >= LeafLevel;
        return new TileInfo(new TileBounds(x0, y0, z0, x0 + w, y0 + h, z0 + d), count, (int)Math.Min(count, _sampleSize), leaf);
    }

    public int GetChildren(in TileKey key, Span<TileKey> destination)
    {
        if (key.Level >= LeafLevel)
        {
            return 0;
        }

        int n = 0;
        int zs = Dimensions == 3 ? 2 : 1;
        for (int dz = 0; dz < zs; dz++)
        {
            for (int dy = 0; dy < 2; dy++)
            {
                for (int dx = 0; dx < 2; dx++)
                {
                    destination[n++] = key.Child(dx, dy, dz);
                }
            }
        }

        return n;
    }
}
