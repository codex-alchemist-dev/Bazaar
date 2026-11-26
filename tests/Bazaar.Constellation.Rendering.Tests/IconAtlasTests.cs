using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class IconAtlasTests
{
    private static readonly IconGlyph[] _allGlyphs = Enum.GetValues<IconGlyph>();

    private static byte[] CellBytes(IconAtlas atlas, IconGlyph glyph)
    {
        AtlasCell c = atlas.GetCell(glyph);
        ReadOnlySpan<byte> all = atlas.Pixels.Span;
        byte[] bytes = new byte[c.Size * c.Size];
        for (int y = 0; y < c.Size; y++)
        {
            all.Slice(((c.Y + y) * atlas.Width) + c.X, c.Size).CopyTo(bytes.AsSpan(y * c.Size));
        }

        return bytes;
    }

    [Fact]
    public void Default_atlas_has_expected_dimensions()
    {
        IconAtlas atlas = IconAtlas.Create();
        Assert.Equal(64, atlas.CellSize);
        Assert.Equal(192, atlas.Width);
        Assert.Equal(192, atlas.Height);
        Assert.Equal(192 * 192, atlas.Pixels.Length);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        Assert.True(IconAtlas.Create().Pixels.Span.SequenceEqual(IconAtlas.Create().Pixels.Span));
        Assert.True(IconAtlas.Create(32, 4).Pixels.Span.SequenceEqual(IconAtlas.Create(32, 4).Pixels.Span));
    }

    [Fact]
    public void Cell_size_and_spread_are_configurable()
    {
        IconAtlas atlas = IconAtlas.Create(32, 4);
        Assert.Equal(96, atlas.Width);
        Assert.Equal(4, atlas.Spread);
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(64, 0)]
    [InlineData(64, 17)]
    public void Invalid_configuration_is_rejected(int cell, int spread)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => IconAtlas.Create(cell, spread));
    }

    [Fact]
    public void Every_glyph_has_inside_and_outside_pixels()
    {
        IconAtlas atlas = IconAtlas.Create();
        foreach (IconGlyph glyph in _allGlyphs)
        {
            byte[] cell = CellBytes(atlas, glyph);
            Assert.Contains(cell, b => b > 128);
            Assert.Contains(cell, b => b < 128);
        }
    }

    [Fact]
    public void Every_glyph_is_distinct()
    {
        IconAtlas atlas = IconAtlas.Create();
        List<byte[]> cells = _allGlyphs.Select(g => CellBytes(atlas, g)).ToList();
        for (int i = 0; i < cells.Count; i++)
        {
            for (int j = i + 1; j < cells.Count; j++)
            {
                Assert.False(cells[i].AsSpan().SequenceEqual(cells[j]), $"{_allGlyphs[i]} equals {_allGlyphs[j]}");
            }
        }
    }

    [Fact]
    public void Sign_convention_holds_at_centre_and_corner()
    {
        IconAtlas atlas = IconAtlas.Create();
        byte[] px = atlas.Pixels.ToArray();
        foreach (IconGlyph glyph in new[] { IconGlyph.Generic, IconGlyph.Dot, IconGlyph.Square })
        {
            AtlasCell c = atlas.GetCell(glyph);
            Assert.True(px[((c.Y + (c.Size / 2)) * atlas.Width) + c.X + (c.Size / 2)] > 128, $"{glyph} centre");
            Assert.True(px[(c.Y * atlas.Width) + c.X] < 128, $"{glyph} corner");
        }
    }

    [Fact]
    public void Field_saturates_far_from_the_edge()
    {
        IconAtlas atlas = IconAtlas.Create();
        AtlasCell c = atlas.GetCell(IconGlyph.Dot);
        byte[] px = atlas.Pixels.ToArray();
        Assert.Equal(1, px[(c.Y * atlas.Width) + c.X]);
        Assert.Equal(255, px[((c.Y + (c.Size / 2)) * atlas.Width) + c.X + (c.Size / 2)]);
    }

    [Fact]
    public void Cells_are_inside_bounds_and_do_not_overlap()
    {
        IconAtlas atlas = IconAtlas.Create(48, 6);
        List<AtlasCell> cells = _allGlyphs.Select(atlas.GetCell).ToList();
        foreach (AtlasCell c in cells)
        {
            Assert.InRange(c.X, 0, atlas.Width - c.Size);
            Assert.InRange(c.Y, 0, atlas.Height - c.Size);
            Assert.InRange(c.U0, 0f, 1f);
            Assert.InRange(c.V1, 0f, 1f);
            Assert.True(c.U0 < c.U1 && c.V0 < c.V1);
        }

        for (int i = 0; i < cells.Count; i++)
        {
            for (int j = i + 1; j < cells.Count; j++)
            {
                bool apart = cells[i].X + cells[i].Size <= cells[j].X || cells[j].X + cells[j].Size <= cells[i].X
                    || cells[i].Y + cells[i].Size <= cells[j].Y || cells[j].Y + cells[j].Size <= cells[i].Y;
                Assert.True(apart);
            }
        }
    }

    [Fact]
    public void L4_is_the_dot_for_every_kind_and_L3_the_square()
    {
        IconAtlas atlas = IconAtlas.Create();
        AtlasCell dot = atlas.GetCell(IconGlyph.Dot);
        AtlasCell square = atlas.GetCell(IconGlyph.Square);
        foreach (NodeKind kind in Enum.GetValues<NodeKind>())
        {
            Assert.True(atlas.TryGetUv(kind, NodeDetailLevel.L4, out AtlasCell l4));
            Assert.Equal(dot, l4);
            Assert.True(atlas.TryGetUv(kind, NodeDetailLevel.L3, out AtlasCell l3));
            Assert.Equal(square, l3);
        }
    }

    [Fact]
    public void Near_rungs_use_the_kind_glyph_and_unknown_kinds_fall_back_to_generic()
    {
        IconAtlas atlas = IconAtlas.Create();
        foreach (NodeKind kind in Enum.GetValues<NodeKind>())
        {
            foreach (NodeDetailLevel level in new[] { NodeDetailLevel.L0, NodeDetailLevel.L1, NodeDetailLevel.L2 })
            {
                Assert.True(atlas.TryGetUv(kind, level, out AtlasCell cell));
                Assert.Equal(atlas.GetCell((IconGlyph)(byte)kind), cell);
            }
        }

        Assert.True(atlas.TryGetUv((NodeKind)200, NodeDetailLevel.L0, out AtlasCell unknown));
        Assert.Equal(atlas.GetCell(IconGlyph.Generic), unknown);
        Assert.False(atlas.TryGetUv(NodeKind.Document, (NodeDetailLevel)9, out _));
    }
}
