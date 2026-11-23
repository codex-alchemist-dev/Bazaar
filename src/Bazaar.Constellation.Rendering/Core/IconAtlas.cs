namespace Bazaar.Constellation.Rendering.Core;

/// <summary>The glyph cells in the icon atlas: one per <see cref="NodeKind"/> plus the two far-rung markers.</summary>
public enum IconGlyph : byte
{
    Generic = 0,
    Document = 1,
    Idea = 2,
    NoteInSpace = 3,
    Label = 4,
    Image = 5,
    Group = 6,

    /// <summary>Plain dot, drawn at the farthest rung for every kind.</summary>
    Dot = 7,

    /// <summary>Square marker, drawn at the L3 rung.</summary>
    Square = 8,
}

/// <summary>A cell rectangle in the atlas: pixel position and size, plus the same rectangle as 0..1 texture coordinates.</summary>
public readonly record struct AtlasCell(int X, int Y, int Size, float U0, float V0, float U1, float V1);

/// <summary>
/// Procedurally generated single-channel signed distance field atlas of simple vector glyphs, one per node kind plus a dot
/// and a square marker. Value 128 is the glyph edge, above 128 is inside and below is outside, saturating at
/// <c>spread</c> pixels from the edge. Output is deterministic and nothing is read from files. Doc:
/// docs/design/constellation-node-detail-and-pacing.md section 3 (SDF atlas format PROPOSED, configurable).
/// </summary>
public sealed class IconAtlas
{
    public const int DefaultCellSize = 64;
    public const int DefaultSpread = 8;

    /// <summary>The atlas is a square grid of cells, row-major in <see cref="IconGlyph"/> order.</summary>
    private const int _columns = 3;
    private const int _rows = 3;

    private readonly byte[] _pixels;

    private IconAtlas(int cellSize, int spread, byte[] pixels)
    {
        CellSize = cellSize;
        Spread = spread;
        _pixels = pixels;
    }

    /// <summary>Edge length of one square cell in pixels.</summary>
    public int CellSize { get; }

    /// <summary>Distance in pixels from the edge at which the field saturates.</summary>
    public int Spread { get; }

    public int Width => _columns * CellSize;

    public int Height => _rows * CellSize;

    /// <summary>The atlas bytes, row-major, one byte per pixel, <see cref="Width"/> by <see cref="Height"/>.</summary>
    public ReadOnlyMemory<byte> Pixels => _pixels;

    /// <summary>Generates the atlas. Throws when the cell is too small to hold a glyph or the spread leaves no room for one.</summary>
    public static IconAtlas Create(int cellSize = DefaultCellSize, int spread = DefaultSpread)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSize, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(spread, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(spread, cellSize / 4);

        int width = _columns * cellSize;
        byte[] pixels = new byte[width * _rows * cellSize];
        float radius = (cellSize / 2f) - spread;

        for (int glyph = 0; glyph < _columns * _rows; glyph++)
        {
            int originX = glyph % _columns * cellSize;
            int originY = glyph / _columns * cellSize;
            for (int y = 0; y < cellSize; y++)
            {
                for (int x = 0; x < cellSize; x++)
                {
                    // Unit space: glyph box is -1..1, y down, sampled at the pixel centre.
                    float ux = (x + 0.5f - (cellSize / 2f)) / radius;
                    float uy = (y + 0.5f - (cellSize / 2f)) / radius;
                    float insidePixels = -Distance((IconGlyph)glyph, ux, uy) * radius;
                    float t = Math.Clamp(insidePixels / spread, -1f, 1f);
                    pixels[((originY + y) * width) + originX + x] = (byte)MathF.Round(128f + (t * 127f));
                }
            }
        }

        return new IconAtlas(cellSize, spread, pixels);
    }

    /// <summary>The glyph drawn for a kind on a rung: the kind icon for L0 to L2, the square marker at L3, the dot at L4.</summary>
    public static IconGlyph GlyphFor(NodeKind kind, NodeDetailLevel level) => level switch
    {
        NodeDetailLevel.L4 => IconGlyph.Dot,
        NodeDetailLevel.L3 => IconGlyph.Square,
        _ => kind switch
        {
            NodeKind.Document => IconGlyph.Document,
            NodeKind.Idea => IconGlyph.Idea,
            NodeKind.NoteInSpace => IconGlyph.NoteInSpace,
            NodeKind.Label => IconGlyph.Label,
            NodeKind.Image => IconGlyph.Image,
            NodeKind.Group => IconGlyph.Group,
            _ => IconGlyph.Generic,
        },
    };

    /// <summary>The cell for a glyph.</summary>
    public AtlasCell GetCell(IconGlyph glyph)
    {
        int index = (int)glyph;
        int x = index % _columns * CellSize;
        int y = index / _columns * CellSize;
        return new AtlasCell(x, y, CellSize, (float)x / Width, (float)y / Height, (float)(x + CellSize) / Width, (float)(y + CellSize) / Height);
    }

    /// <summary>Finds the cell to draw for a kind on a rung. False when the rung is not a defined one.</summary>
    public bool TryGetUv(NodeKind kind, NodeDetailLevel level, out AtlasCell cell)
    {
        if (level > NodeDetailLevel.L4)
        {
            cell = default;
            return false;
        }

        cell = GetCell(GlyphFor(kind, level));
        return true;
    }

    // Signed distance in unit space, negative inside. Combinations (min = union, max = intersection) are bounds, not exact.
    private static float Distance(IconGlyph glyph, float x, float y) => glyph switch
    {
        IconGlyph.Document => DocumentDistance(x, y),
        IconGlyph.Idea => Math.Min(Circle(x, y + 0.2f, 0.5f), Box(x, y - 0.6f, 0.22f, 0.2f)),
        IconGlyph.NoteInSpace => Math.Max(Box(x, y, 0.75f, 0.75f), -Circle(x, y + 0.5f, 0.1f)),
        IconGlyph.Label => Math.Max(Box(x, y, 0.8f, 0.45f), -Circle(x + 0.5f, y, 0.12f)),
        IconGlyph.Image => Math.Min(
            Math.Max(Box(x, y, 0.8f, 0.65f), -Box(x, y, 0.62f, 0.47f)),
            Circle(x - 0.3f, y + 0.2f, 0.15f)),
        IconGlyph.Group => Math.Max(
            Math.Max(Box(x, y, 0.8f, 0.8f), -Box(x, y, 0.62f, 0.62f)),
            Math.Max(-Box(x, y, 0.3f, 1f), -Box(x, y, 1f, 0.3f))),
        IconGlyph.Dot => Circle(x, y, 0.55f),
        IconGlyph.Square => Box(x, y, 0.55f, 0.55f),
        _ => Circle(x, y, 0.8f),
    };

    // Page with the top-right corner cut off and a fold line along the cut.
    private static float DocumentDistance(float x, float y)
    {
        float page = Math.Max(Box(x, y, 0.6f, 0.8f), (x - y - 1f) * 0.70710678f);
        float fold = Math.Min(Segment(x, y, 0.2f, -0.8f, 0.2f, -0.4f), Segment(x, y, 0.2f, -0.4f, 0.6f, -0.4f)) - 0.05f;
        return Math.Max(page, -fold);
    }

    private static float Circle(float x, float y, float r) => MathF.Sqrt((x * x) + (y * y)) - r;

    private static float Box(float x, float y, float halfX, float halfY)
    {
        float dx = MathF.Abs(x) - halfX;
        float dy = MathF.Abs(y) - halfY;
        float outside = MathF.Sqrt((MathF.Max(dx, 0f) * MathF.Max(dx, 0f)) + (MathF.Max(dy, 0f) * MathF.Max(dy, 0f)));
        return outside + MathF.Min(MathF.Max(dx, dy), 0f);
    }

    private static float Segment(float x, float y, float ax, float ay, float bx, float by)
    {
        float abx = bx - ax;
        float aby = by - ay;
        float t = Math.Clamp((((x - ax) * abx) + ((y - ay) * aby)) / ((abx * abx) + (aby * aby)), 0f, 1f);
        float dx = x - (ax + (t * abx));
        float dy = y - (ay + (t * aby));
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
