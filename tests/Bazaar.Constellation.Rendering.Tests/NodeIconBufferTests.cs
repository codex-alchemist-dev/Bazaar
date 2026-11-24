using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public sealed class NodeIconBufferTests
{
    private static readonly IconAtlas _atlas = IconAtlas.Create();

    [Fact]
    public void Large_document_gets_the_document_cell()
    {
        var buffer = new NodeIconBuffer();
        buffer.Fill([NodeKind.Document], [300f], 1f, _atlas);

        AtlasCell cell = _atlas.GetCell(IconGlyph.Document);
        Assert.Equal([cell.U0, cell.V0, cell.U1, cell.V1], buffer.Floats.ToArray());
        Assert.Equal(NodeDetailLevel.L0, buffer.Levels[0]);
    }

    [Fact]
    public void Mid_size_picks_the_square_marker_and_tiny_is_a_dot_with_no_icon()
    {
        var buffer = new NodeIconBuffer();
        buffer.Fill([NodeKind.Document, NodeKind.Document], [12f, 2f], 1f, _atlas);

        AtlasCell square = _atlas.GetCell(IconGlyph.Square);
        Assert.Equal(NodeDetailLevel.L3, buffer.Levels[0]);
        Assert.Equal(square.U0, buffer.Floats[0]);
        Assert.Equal(NodeDetailLevel.L4, buffer.Levels[1]);
        Assert.All(buffer.Floats[4..8].ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Disabled_writes_zero_rectangles_for_every_node()
    {
        var buffer = new NodeIconBuffer();
        buffer.Fill([NodeKind.Idea, NodeKind.Label], [300f, 300f], 1f, _atlas, enabled: false);

        Assert.Equal(2, buffer.InstanceCount);
        Assert.Equal(2 * NodeIconBuffer.FloatsPerInstance * sizeof(float), buffer.Bytes.Length);
        Assert.All(buffer.Floats.ToArray(), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Lower_detail_scale_simplifies_sooner()
    {
        var full = new NodeIconBuffer();
        var low = new NodeIconBuffer();
        full.Fill([NodeKind.Document], [60f], 1f, _atlas);
        low.Fill([NodeKind.Document], [60f], 0.25f, _atlas);

        Assert.True(low.Levels[0] > full.Levels[0]);
    }

    [Fact]
    public void Hysteresis_holds_the_previous_rung_near_a_cutoff()
    {
        var buffer = new NodeIconBuffer();
        buffer.Fill([NodeKind.Document], [130f], 1f, _atlas);
        Assert.Equal(NodeDetailLevel.L1, buffer.Levels[0]);

        // 245 is above the 240 L0 cutoff but inside the 10 percent margin, so the node stays on L1.
        buffer.Fill([NodeKind.Document], [245f], 1f, _atlas);
        Assert.Equal(NodeDetailLevel.L1, buffer.Levels[0]);
    }

    [Fact]
    public void Mismatched_lengths_are_rejected()
    {
        var buffer = new NodeIconBuffer();
        Assert.Throws<ArgumentException>(() => buffer.Fill([NodeKind.Idea], [1f, 2f], 1f, _atlas));
    }
}
