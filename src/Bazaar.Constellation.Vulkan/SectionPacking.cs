using System.Runtime.InteropServices;
using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Packs <see cref="SectionRect"/> values into the per-instance bytes the section shaders read: min x, min y, max x, max y
/// (world space), then colour r, g, b, a, eight floats and 32 bytes per section. Colour is read as 0xRRGGBB with any high byte
/// ignored, and alpha comes from the rectangle (already scaled by the cull and fade step). Builds no device objects, so the
/// layout can be checked without a GPU. Locations: 0 corner (binding 0), 1 bounds and 2 colour (binding 1).
/// </summary>
public static class SectionPacking
{
    /// <summary>Floats per section instance: four bounds, four colour components.</summary>
    public const int FloatsPerSection = 8;

    /// <summary>Bytes per section instance.</summary>
    public const int SectionStride = FloatsPerSection * sizeof(float);

    /// <summary>Bytes per corner vertex (vec2).</summary>
    public const uint CornerStride = 2 * sizeof(float);

    public static VkVertexInputBindingDescription[] Bindings =>
    [
        new VkVertexInputBindingDescription { binding = 0, stride = CornerStride, inputRate = VkVertexInputRate.Vertex },
        new VkVertexInputBindingDescription { binding = 1, stride = SectionStride, inputRate = VkVertexInputRate.Instance },
    ];

    public static VkVertexInputAttributeDescription[] Attributes =>
    [
        new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0 },
        new VkVertexInputAttributeDescription { location = 1, binding = 1, format = VkFormat.R32G32B32A32Sfloat, offset = 0 },
        new VkVertexInputAttributeDescription { location = 2, binding = 1, format = VkFormat.R32G32B32A32Sfloat, offset = 4 * sizeof(float) },
    ];

    /// <summary>Packs the sections in order, so draw order (parents first) is kept. An empty list gives empty bytes.</summary>
    public static byte[] Pack(IReadOnlyList<SectionRect> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var floats = new float[sections.Count * FloatsPerSection];
        for (int index = 0; index < sections.Count; index++)
        {
            SectionRect rect = sections[index];
            int target = index * FloatsPerSection;
            floats[target] = rect.MinX;
            floats[target + 1] = rect.MinY;
            floats[target + 2] = rect.MaxX;
            floats[target + 3] = rect.MaxY;
            floats[target + 4] = ((rect.Color >> 16) & 0xFF) / 255f;
            floats[target + 5] = ((rect.Color >> 8) & 0xFF) / 255f;
            floats[target + 6] = (rect.Color & 0xFF) / 255f;
            floats[target + 7] = Math.Clamp(rect.Alpha, 0f, 1f);
        }

        return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
    }
}
