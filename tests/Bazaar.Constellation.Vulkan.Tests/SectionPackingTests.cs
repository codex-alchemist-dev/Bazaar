using System.Runtime.InteropServices;
using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Tests;

public class SectionPackingTests
{
    private static SectionRect Rect(string id, uint color, float alpha, float minX = -1f, float minY = -2f, float maxX = 3f, float maxY = 4f) =>
        new(id, id, null, 0, color, minX, minY, maxX, maxY, alpha, true);

    [Fact]
    public void Stride_is_eight_floats()
    {
        Assert.Equal(8, SectionPacking.FloatsPerSection);
        Assert.Equal(32, SectionPacking.SectionStride);
    }

    [Fact]
    public void Empty_input_packs_to_no_bytes()
    {
        Assert.Empty(SectionPacking.Pack([]));
    }

    [Fact]
    public void Pack_writes_bounds_then_colour_and_alpha_per_section_in_order()
    {
        byte[] bytes = SectionPacking.Pack([Rect("a", 0xFF8000u, 0.25f), Rect("b", 0x0000FFu, 1f, 5f, 6f, 7f, 8f)]);
        Assert.Equal(2 * SectionPacking.SectionStride, bytes.Length);
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(bytes);
        Assert.Equal([-1f, -2f, 3f, 4f], f[..4].ToArray());
        Assert.Equal(1f, f[4]);
        Assert.Equal(128f / 255f, f[5], 5);
        Assert.Equal(0f, f[6]);
        Assert.Equal(0.25f, f[7]);
        Assert.Equal([5f, 6f, 7f, 8f], f.Slice(8, 4).ToArray());
        Assert.Equal(0f, f[12]);
        Assert.Equal(1f, f[14]);
        Assert.Equal(1f, f[15]);
    }

    [Fact]
    public void Alpha_is_clamped_and_the_high_colour_byte_is_ignored()
    {
        byte[] bytes = SectionPacking.Pack([Rect("a", 0x80FFFFFFu, 3f), Rect("b", 0u, -1f)]);
        ReadOnlySpan<float> f = MemoryMarshal.Cast<byte, float>(bytes);
        Assert.Equal(1f, f[4]);
        Assert.Equal(1f, f[7]);
        Assert.Equal(0f, f[15]);
    }

    [Fact]
    public void Vertex_layout_matches_the_packed_stride_and_shader_locations()
    {
        VkVertexInputBindingDescription[] bindings = SectionPacking.Bindings;
        Assert.Equal(SectionPacking.CornerStride, bindings[0].stride);
        Assert.Equal((uint)SectionPacking.SectionStride, bindings[1].stride);
        Assert.Equal(VkVertexInputRate.Instance, bindings[1].inputRate);
        VkVertexInputAttributeDescription[] attributes = SectionPacking.Attributes;
        Assert.Equal([0u, 1u, 2u], attributes.Select(a => a.location).ToArray());
        Assert.Equal(0u, attributes[1].offset);
        Assert.Equal(16u, attributes[2].offset);
    }

    [Fact]
    public void Section_shaders_are_embedded_spirv()
    {
        foreach (string name in new[] { "section.vert.spv", "section.frag.spv" })
        {
            using Stream? stream = typeof(SectionPacking).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{name}");
            Assert.NotNull(stream);
            var magic = new byte[4];
            Assert.Equal(4, stream.Read(magic));
            Assert.Equal(0x07230203u, BitConverter.ToUInt32(magic));
        }
    }
}
