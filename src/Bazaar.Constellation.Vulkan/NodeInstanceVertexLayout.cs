using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Vertex input description for the node instanced-quad pipeline. Binding 0 is the per-vertex unit-square corner
/// (vec2); binding 1 is the per-instance block that <see cref="NodeInstanceBuffer"/> packs (x, y, style index).
/// The shaders in Shaders/ read these locations: 0 corner, 1 centre, 2 style index. The style itself comes from the
/// style table in a storage buffer, see <see cref="NodeStylePacking"/>.
/// This type builds no device objects, so the layout can be checked without a GPU.
/// </summary>
public static class NodeInstanceVertexLayout
{
    /// <summary>Corners of the unit square as a triangle strip: (-1,-1), (1,-1), (-1,1), (1,1).</summary>
    public static ReadOnlySpan<float> QuadCorners => [-1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f];

    /// <summary>Bytes per instance, equal to <see cref="NodeInstanceBuffer.FloatsPerInstance"/> times four.</summary>
    public const uint InstanceStride = NodeInstanceBuffer.FloatsPerInstance * sizeof(float);

    /// <summary>Bytes per corner vertex.</summary>
    public const uint CornerStride = 2 * sizeof(float);

    public static VkVertexInputBindingDescription[] Bindings =>
    [
        new VkVertexInputBindingDescription { binding = 0, stride = CornerStride, inputRate = VkVertexInputRate.Vertex },
        new VkVertexInputBindingDescription { binding = 1, stride = InstanceStride, inputRate = VkVertexInputRate.Instance },
    ];

    public static VkVertexInputAttributeDescription[] Attributes =>
    [
        new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0 },
        new VkVertexInputAttributeDescription { location = 1, binding = 1, format = VkFormat.R32G32Sfloat, offset = 0 },
        new VkVertexInputAttributeDescription { location = 2, binding = 1, format = VkFormat.R32Sfloat, offset = 2 * sizeof(float) },
    ];
}
