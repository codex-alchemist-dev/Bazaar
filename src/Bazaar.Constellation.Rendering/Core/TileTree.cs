namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Address of one tile in an implicit quadtree (2D, z is 0) or octree (3D). Level 0 is the root.
/// Doc: docs/design/constellation-scale-and-lod.md section 3.
/// </summary>
public readonly record struct TileKey(int Level, long X, long Y, long Z)
{
    public static TileKey Root => new(0, 0, 0, 0);

    public TileKey Child(int dx, int dy, int dz) => new(Level + 1, (X * 2) + dx, (Y * 2) + dy, (Z * 2) + dz);
}

/// <summary>Axis-aligned world box. A 2D scene uses z = 0 for both ends.</summary>
public readonly record struct TileBounds(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
{
    public float CenterX => (MinX + MaxX) * 0.5f;

    public float CenterY => (MinY + MaxY) * 0.5f;

    public float CenterZ => (MinZ + MaxZ) * 0.5f;

    public float Radius
    {
        get
        {
            float dx = MaxX - MinX;
            float dy = MaxY - MinY;
            float dz = MaxZ - MinZ;
            return 0.5f * MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
    }
}

/// <summary>What the selector needs to know about a tile. <paramref name="SampleCount"/> is what a coarse draw of the tile costs.</summary>
public readonly record struct TileInfo(TileBounds Bounds, long NodeCount, int SampleCount, bool IsLeaf);

/// <summary>A tile hierarchy the selector can walk. The server-built tiles and the synthetic benchmark tree both implement it.</summary>
public interface ITileTree
{
    int Dimensions { get; }

    TileInfo GetInfo(in TileKey key);

    /// <summary>Writes the non-empty children into <paramref name="destination"/> (room for 8) and returns how many.</summary>
    int GetChildren(in TileKey key, Span<TileKey> destination);
}
