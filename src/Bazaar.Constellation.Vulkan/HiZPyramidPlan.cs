using System.Numerics;

namespace Bazaar.Constellation.Vulkan;

/// <summary>What one recorded step of the Hi-Z build is.</summary>
public enum HiZStepKind
{
    /// <summary>Wait for the previous consumer's reads of the pyramid, so this pass may overwrite it (read-then-write).</summary>
    PyramidFromConsumer,

    /// <summary>Depth attachment to shader-read-only layout, after the depth writes of the render pass.</summary>
    DepthToReadOnly,

    /// <summary>One compute dispatch: reads <see cref="HiZStep.SourceMip"/> (or the depth image) and writes <see cref="HiZStep.Mip"/>.</summary>
    Dispatch,

    /// <summary>Make the writes of the mip in <see cref="HiZStep.Mip"/> visible to the next dispatch's reads.</summary>
    MipWriteToRead,

    /// <summary>Make the whole pyramid visible to the consumer (the culling compute pass of the next frame).</summary>
    PyramidToConsumer,

    /// <summary>Depth image back to attachment layout for the next frame's render pass.</summary>
    DepthToAttachment,
}

/// <summary>One step of the Hi-Z build. <see cref="Mip"/> is the mip written or made visible; <see cref="SourceMip"/> is -1 for the depth image.</summary>
public readonly record struct HiZStep(HiZStepKind Kind, int Mip = -1, int SourceMip = -1);

/// <summary>
/// Pure size and ordering rules for the Hi-Z pyramid, kept apart from Vulkan so they can be tested without a GPU.
/// The pyramid's mip 0 is half the depth image (rounded up): the first dispatch reduces the depth image straight into it,
/// so no full-resolution copy is made. Mips after that halve with the usual Vulkan rule (round down, never below one).
/// </summary>
public static class HiZPyramidPlan
{
    /// <summary>Threads per group in each direction; must match <c>local_size</c> in hiz_downsample.comp.glsl.</summary>
    public const uint GroupSize = 8;

    /// <summary>Extent of pyramid mip 0 for a depth image: half, rounded up, at least one texel.</summary>
    public static (uint Width, uint Height) BaseExtent(uint depthWidth, uint depthHeight)
    {
        ArgumentOutOfRangeException.ThrowIfZero(depthWidth);
        ArgumentOutOfRangeException.ThrowIfZero(depthHeight);
        return (Math.Max(1u, (depthWidth + 1) / 2), Math.Max(1u, (depthHeight + 1) / 2));
    }

    /// <summary>Number of mips in a full chain down to 1x1 for a base of this size.</summary>
    public static uint MipCount(uint baseWidth, uint baseHeight)
    {
        ArgumentOutOfRangeException.ThrowIfZero(baseWidth);
        ArgumentOutOfRangeException.ThrowIfZero(baseHeight);
        return (uint)BitOperations.Log2(Math.Max(baseWidth, baseHeight)) + 1;
    }

    /// <summary>Extent of a pyramid mip: the base halved <paramref name="mip"/> times, rounding down, never below one.</summary>
    public static (uint Width, uint Height) MipExtent(uint baseWidth, uint baseHeight, uint mip) =>
        (Math.Max(1u, baseWidth >> (int)mip), Math.Max(1u, baseHeight >> (int)mip));

    /// <summary>Workgroups needed to cover an extent in one direction.</summary>
    public static uint GroupCount(uint extent) => (extent + GroupSize - 1) / GroupSize;

    /// <summary>
    /// The ordered steps for one build: the pyramid is released by the previous consumer, the depth image goes read-only,
    /// each mip is dispatched with a visibility barrier before the next one reads it, then the pyramid goes to the consumer
    /// and the depth image back to attachment layout.
    /// </summary>
    public static IReadOnlyList<HiZStep> Steps(uint mipCount)
    {
        ArgumentOutOfRangeException.ThrowIfZero(mipCount);
        var steps = new List<HiZStep>
        {
            new(HiZStepKind.PyramidFromConsumer),
            new(HiZStepKind.DepthToReadOnly),
            new(HiZStepKind.Dispatch, Mip: 0, SourceMip: -1),
        };
        for (int mip = 1; mip < mipCount; mip++)
        {
            steps.Add(new HiZStep(HiZStepKind.MipWriteToRead, mip - 1));
            steps.Add(new HiZStep(HiZStepKind.Dispatch, mip, mip - 1));
        }

        steps.Add(new HiZStep(HiZStepKind.PyramidToConsumer));
        steps.Add(new HiZStep(HiZStepKind.DepthToAttachment));
        return steps;
    }
}
