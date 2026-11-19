using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

public class NodeStylePackingTests
{
    [Fact]
    public void A_style_is_thirty_two_bytes_with_colour_radius_and_ring_width_in_order()
    {
        Assert.Equal(32, NodeStylePacking.StyleStride);

        NodeStylePalette palette = NodeStylePalette.Build(new NodeStyleTable(1, NodeStyle.Create(0.1f, 0.2f, 0.3f, 0.4f, 5f, 0.25f)));
        float[] floats = MemoryMarshalFloats(NodeStylePacking.Pack(palette));

        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 5f, 0.25f, 0f, 0f }, floats);
    }

    [Fact]
    public void Styles_are_packed_in_palette_index_order()
    {
        var table = new NodeStyleTable(2, NodeStyle.Create(1f, 0f, 0f, 1f, 2f));
        table.Set(1, NodeStyle.Create(0f, 1f, 0f, 1f, 3f));
        NodeStylePalette palette = NodeStylePalette.Build(table);

        float[] floats = MemoryMarshalFloats(NodeStylePacking.Pack(palette));

        Assert.Equal(2 * NodeStylePacking.FloatsPerStyle, floats.Length);
        Assert.Equal(1f, floats[0]);   // style 0, red
        Assert.Equal(2f, floats[4]);   // style 0, radius
        Assert.Equal(1f, floats[9]);   // style 1, green
        Assert.Equal(3f, floats[12]);  // style 1, radius
    }

    [Fact]
    public void An_empty_palette_packs_to_no_bytes()
    {
        NodeStylePalette palette = NodeStylePalette.Build(new NodeStyleTable(0, NodeStyle.Create(0f, 0f, 0f, 1f, 1f)));
        Assert.Empty(NodeStylePacking.Pack(palette));
    }

    private static float[] MemoryMarshalFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
