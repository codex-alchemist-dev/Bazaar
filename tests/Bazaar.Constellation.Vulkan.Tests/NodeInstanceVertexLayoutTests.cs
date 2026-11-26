using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Tests;

public class NodeInstanceVertexLayoutTests
{
    [Fact]
    public void Instance_stride_matches_the_packed_buffer() =>
        Assert.Equal((uint)(NodeInstanceBuffer.FloatsPerInstance * sizeof(float)), NodeInstanceVertexLayout.InstanceStride);

    [Fact]
    public void Instance_block_is_centre_then_style_index()
    {
        VkVertexInputAttributeDescription[] attributes = NodeInstanceVertexLayout.Attributes;
        var instance = attributes.Where(a => a.binding == 1).OrderBy(a => a.offset).ToArray();

        Assert.Equal(2, instance.Length);
        Assert.Equal(0u, instance[0].offset);   // centre x, y
        Assert.Equal(8u, instance[1].offset);   // style index
        Assert.Equal(NodeInstanceVertexLayout.InstanceStride, instance[1].offset + 4u);
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
    public void Icon_stream_is_one_vec4_rectangle_per_instance_on_its_own_binding()
    {
        Assert.Equal((uint)(NodeIconBuffer.FloatsPerInstance * sizeof(float)), NodeInstanceVertexLayout.IconStride);
        VkVertexInputBindingDescription binding = Assert.Single(NodeInstanceVertexLayout.Bindings, b => b.binding == 2);
        Assert.Equal(NodeInstanceVertexLayout.IconStride, binding.stride);
        Assert.Equal(VkVertexInputRate.Instance, binding.inputRate);

        VkVertexInputAttributeDescription icon = Assert.Single(NodeInstanceVertexLayout.Attributes, a => a.binding == 2);
        Assert.Equal(3u, icon.location);
        Assert.Equal(VkFormat.R32G32B32A32Sfloat, icon.format);
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
