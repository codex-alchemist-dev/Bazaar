using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>Section rectangles on the physical GPU through the offscreen renderer. Skips where the loader finds no usable device.</summary>
public class VulkanSectionGpuTests
{
    private const uint _size = 64;

    private static VulkanOffscreenRenderer CreateOrSkip()
    {
        try
        {
            return VulkanOffscreenRenderer.Create(_size, _size);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    private static int Offset(int x, int y) => (y * (int)_size + x) * 4;

    [Fact]
    public void Section_blends_over_the_background_and_leaves_the_outside_untouched()
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip();
        // A white section over the left half of the image (NDC x -1..0) at alpha 0.5, no nodes.
        var rect = new SectionRect("s", "s", null, 0, 0xFFFFFFu, -1f, -1f, 0f, 1f, 0.5f, true);
        renderer.SetSections(SectionPacking.Pack([rect]));
        renderer.Render((0f, 0f, 0f, 1f), ReadOnlySpan<byte>.Empty, 0, NodeView.Identity);
        ReadOnlySpan<byte> pixels = renderer.ReadPixels();

        int inside = Offset(16, 32);
        int outside = Offset(48, 32);
        Assert.InRange(pixels[inside], 120, 135);
        Assert.InRange(pixels[inside + 1], 120, 135);
        Assert.InRange(pixels[inside + 2], 120, 135);
        Assert.Equal(0, pixels[outside]);
        Assert.Equal(0, pixels[outside + 1]);
        Assert.Equal(0, pixels[outside + 2]);
    }
}
