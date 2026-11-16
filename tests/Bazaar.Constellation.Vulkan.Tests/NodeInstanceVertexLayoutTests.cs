using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Tests;

public class NodeInstanceVertexLayoutTests
{
    [Fact]
    public void Instance_stride_matches_the_packed_buffer() =>
        Assert.Equal((uint)(NodeInstanceBuffer.FloatsPerInstance * sizeof(float)), NodeInstanceVertexLayout.InstanceStride);

    [Fact]
    public void Attributes_cover_the_packed_fields_without_gaps_or_overlap()
    {
        VkVertexInputAttributeDescription[] attributes = NodeInstanceVertexLayout.Attributes;
        var instance = attributes.Where(a => a.binding == 1).OrderBy(a => a.offset).ToArray();

        Assert.Equal(3, instance.Length);
        Assert.Equal(0u, instance[0].offset);   // centre x, y
        Assert.Equal(8u, instance[1].offset);   // radius
        Assert.Equal(12u, instance[2].offset);  // r, g, b, a
        Assert.Equal(NodeInstanceVertexLayout.InstanceStride, instance[2].offset + 16u);
    }

    [Fact]
    public void Each_location_is_used_once_and_the_bindings_exist()
    {
        VkVertexInputAttributeDescription[] attributes = NodeInstanceVertexLayout.Attributes;
        uint[] bindings = NodeInstanceVertexLayout.Bindings.Select(b => b.binding).ToArray();

        Assert.Equal(attributes.Length, attributes.Select(a => a.location).Distinct().Count());
        Assert.All(attributes, a => Assert.Contains(a.binding, bindings));
    }

    [Fact]
    public void Quad_corners_are_the_unit_square()
    {
        float[] corners = NodeInstanceVertexLayout.QuadCorners.ToArray();
        Assert.Equal(8, corners.Length);
        Assert.All(corners, c => Assert.True(c is -1f or 1f));
    }

    [Fact]
    public void Compiled_shaders_are_spirv_modules()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bazaar.Constellation.Vulkan", "Shaders");
        foreach (string name in new[] { "node.vert.spv", "node.frag.spv" })
        {
            byte[] bytes = File.ReadAllBytes(Path.GetFullPath(Path.Combine(dir, name)));
            Assert.True(bytes.Length >= 20 && bytes.Length % 4 == 0, name);
            Assert.Equal(0x07230203u, BitConverter.ToUInt32(bytes, 0));
        }
    }
}
