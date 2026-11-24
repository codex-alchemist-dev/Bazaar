using System.Runtime.InteropServices;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

public class VulkanNodeIconOptionsTests
{
    [Fact]
    public void Icons_default_on_with_the_default_atlas_cell()
    {
        var options = new VulkanConstellationRendererOptions();
        Assert.True(options.NodeIcons);
        Assert.Equal(IconAtlas.DefaultCellSize, options.IconCellSize);
    }

    [Fact]
    public void Icons_can_be_turned_off_in_options() =>
        Assert.False(new VulkanConstellationRendererOptions { NodeIcons = false }.NodeIcons);
}

/// <summary>Icon drawing on the physical GPU through the offscreen renderer. Skips where the loader finds no usable device.</summary>
public class VulkanNodeIconGpuTests
{
    private const uint _size = 64;

    private static VulkanOffscreenRenderer CreateOrSkip(bool icons)
    {
        try
        {
            return VulkanOffscreenRenderer.Create(_size, _size, nodeIcons: icons);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    private static int Offset(int x, int y) => (y * (int)_size + x) * 4;

    /// <summary>One white node of NDC radius 0.8 at the origin, drawn with the given icon rectangle, then the pixels read back.</summary>
    private static byte[] RenderWithRect(VulkanOffscreenRenderer renderer, float[] rect)
    {
        var styles = new NodeStyleTable(1, NodeStyle.Create(1f, 1f, 1f, 1f, 0.8f));
        NodeStylePalette palette = NodeStylePalette.Build(styles);
        renderer.SetStyles(NodeStylePacking.Pack(palette));
        var instances = new NodeInstanceBuffer();
        instances.Fill([0f, 0f], 2, palette);
        renderer.SetIcons(MemoryMarshal.AsBytes<float>(rect));
        renderer.Render((0f, 0f, 0f, 1f), instances.Bytes, instances.InstanceCount, NodeView.Identity);
        return renderer.ReadPixels().ToArray();
    }

    [Fact]
    public void Square_marker_is_smaller_than_the_dot_it_replaces()
    {
        var atlas = IconAtlas.Create();
        AtlasCell square = atlas.GetCell(IconGlyph.Square);
        float[] rect = [square.U0, square.V0, square.U1, square.V1];

        // Pixel (48, 48) sits at about 0.6 of the radius on both axes: inside the disc, outside the square glyph.
        using (VulkanOffscreenRenderer on = CreateOrSkip(icons: true))
        {
            byte[] pixels = RenderWithRect(on, rect);
            Assert.True(pixels[Offset(32, 32)] > 200, "square centre should be filled");
            Assert.True(pixels[Offset(48, 48)] < 20, "point outside the square glyph should be background");
        }

        using (VulkanOffscreenRenderer off = CreateOrSkip(icons: false))
        {
            byte[] pixels = RenderWithRect(off, rect);
            Assert.True(pixels[Offset(48, 48)] > 200, "with icons off the node stays a plain disc");
        }
    }

    [Fact]
    public void Zero_rectangle_draws_the_plain_disc()
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip(icons: true);
        byte[] pixels = RenderWithRect(renderer, [0f, 0f, 0f, 0f]);
        Assert.True(pixels[Offset(48, 48)] > 200);
    }
}
