using System.Runtime.InteropServices;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Packs a <see cref="NodeStylePalette"/> into the std430 layout the shaders read from the style storage buffer.
/// One style is eight floats, 32 bytes: colour (r, g, b, a), radius, ring width, and two padding floats.
/// This type builds no device objects, so the layout can be checked without a GPU.
/// </summary>
public static class NodeStylePacking
{
    /// <summary>Floats per style: four colour components, radius, ring width, two padding.</summary>
    public const int FloatsPerStyle = 8;

    /// <summary>Bytes per style in the storage buffer.</summary>
    public const int StyleStride = FloatsPerStyle * sizeof(float);

    /// <summary>Packs every style of the palette in index order. Entry i of the result describes style i.</summary>
    public static byte[] Pack(NodeStylePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var floats = new float[palette.StyleCount * FloatsPerStyle];
        for (int index = 0; index < palette.StyleCount; index++)
        {
            NodeStyle style = palette.GetStyle(index);
            int target = index * FloatsPerStyle;
            floats[target] = style.Red;
            floats[target + 1] = style.Green;
            floats[target + 2] = style.Blue;
            floats[target + 3] = style.Alpha;
            floats[target + 4] = style.Radius;
            floats[target + 5] = style.RingWidth;
            floats[target + 6] = 0f;
            floats[target + 7] = 0f;
        }

        return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
    }
}
