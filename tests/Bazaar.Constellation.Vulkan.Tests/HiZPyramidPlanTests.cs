using System.Buffers.Binary;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Tests;

public class HiZPyramidPlanTests
{
    [Theory]
    [InlineData(1920u, 1080u, 960u, 540u)]
    [InlineData(1921u, 1081u, 961u, 541u)]
    [InlineData(1u, 1u, 1u, 1u)]
    [InlineData(2u, 1u, 1u, 1u)]
    public void Base_extent_is_half_the_depth_image_rounded_up(uint depthW, uint depthH, uint baseW, uint baseH)
    {
        Assert.Equal((baseW, baseH), HiZPyramidPlan.BaseExtent(depthW, depthH));
    }

    [Theory]
    [InlineData(1u, 1u, 1u)]
    [InlineData(2u, 2u, 2u)]
    [InlineData(960u, 540u, 10u)]
    [InlineData(1920u, 1080u, 11u)]
    [InlineData(1u, 512u, 10u)]
    public void Mip_count_runs_the_chain_down_to_one_texel(uint width, uint height, uint expected)
    {
        Assert.Equal(expected, HiZPyramidPlan.MipCount(width, height));
    }

    [Fact]
    public void The_last_mip_is_one_by_one_and_extents_never_reach_zero()
    {
        foreach ((uint w, uint h) in new[] { (960u, 540u), (1u, 700u), (333u, 7u), (1u, 1u) })
        {
            uint count = HiZPyramidPlan.MipCount(w, h);
            Assert.Equal((1u, 1u), HiZPyramidPlan.MipExtent(w, h, count - 1));
        }
    }

    [Fact]
    public void Mip_extents_halve_rounding_down()
    {
        Assert.Equal((960u, 540u), HiZPyramidPlan.MipExtent(960, 540, 0));
        Assert.Equal((480u, 270u), HiZPyramidPlan.MipExtent(960, 540, 1));
        Assert.Equal((240u, 135u), HiZPyramidPlan.MipExtent(960, 540, 2));
        Assert.Equal((120u, 67u), HiZPyramidPlan.MipExtent(960, 540, 3));
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(8u, 1u)]
    [InlineData(9u, 2u)]
    [InlineData(960u, 120u)]
    public void Group_count_rounds_up_to_the_eight_wide_workgroup(uint extent, uint groups)
    {
        Assert.Equal(groups, HiZPyramidPlan.GroupCount(extent));
    }

    [Fact]
    public void Every_dispatch_reads_the_mip_above_and_depth_feeds_the_first()
    {
        IReadOnlyList<HiZStep> steps = HiZPyramidPlan.Steps(5);
        HiZStep[] dispatches = steps.Where(s => s.Kind == HiZStepKind.Dispatch).ToArray();

        Assert.Equal(5, dispatches.Length);
        Assert.Equal(-1, dispatches[0].SourceMip);
        for (int i = 0; i < dispatches.Length; i++)
        {
            Assert.Equal(i, dispatches[i].Mip);
            if (i > 0)
            {
                Assert.Equal(i - 1, dispatches[i].SourceMip);
            }
        }
    }

    [Fact]
    public void A_visibility_barrier_for_the_source_mip_sits_between_consecutive_dispatches()
    {
        IReadOnlyList<HiZStep> steps = HiZPyramidPlan.Steps(6);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind == HiZStepKind.Dispatch && steps[i].SourceMip >= 0)
            {
                HiZStep before = steps[i - 1];
                Assert.Equal(HiZStepKind.MipWriteToRead, before.Kind);
                Assert.Equal(steps[i].SourceMip, before.Mip);
                Assert.Equal(HiZStepKind.Dispatch, steps[i - 2].Kind);
                Assert.Equal(steps[i].SourceMip, steps[i - 2].Mip);
            }
        }
    }

    [Fact]
    public void The_build_opens_with_consumer_release_then_depth_to_read_only_and_closes_with_hand_offs()
    {
        IReadOnlyList<HiZStep> steps = HiZPyramidPlan.Steps(3);

        Assert.Equal(HiZStepKind.PyramidFromConsumer, steps[0].Kind);
        Assert.Equal(HiZStepKind.DepthToReadOnly, steps[1].Kind);
        Assert.Equal(HiZStepKind.Dispatch, steps[2].Kind);
        Assert.Equal(HiZStepKind.PyramidToConsumer, steps[^2].Kind);
        Assert.Equal(HiZStepKind.DepthToAttachment, steps[^1].Kind);
    }

    [Fact]
    public void A_single_mip_chain_needs_no_mip_barrier()
    {
        IReadOnlyList<HiZStep> steps = HiZPyramidPlan.Steps(1);

        Assert.DoesNotContain(steps, s => s.Kind == HiZStepKind.MipWriteToRead);
        Assert.Single(steps, s => s.Kind == HiZStepKind.Dispatch);
    }

    [Fact]
    public void Zero_sizes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HiZPyramidPlan.BaseExtent(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => HiZPyramidPlan.MipCount(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HiZPyramidPlan.Steps(0));
    }

    [Fact]
    public void The_embedded_downsample_shader_is_a_well_formed_spirv_module()
    {
        using Stream stream = typeof(ReversedZDepth).Assembly.GetManifestResourceStream("Bazaar.Constellation.Vulkan.Shaders.hiz_downsample.comp.spv")
            ?? throw new InvalidOperationException("hiz_downsample.comp.spv is not embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();

        Assert.Equal(0, bytes.Length % 4);
        var words = new uint[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        Assert.Equal(0x07230203u, words[0]);
        uint bound = words[3];
        int cursor = 5;
        while (cursor < words.Length)
        {
            int length = (int)(words[cursor] >> 16);
            Assert.True(length > 0, "SPIR-V instruction with zero length.");
            cursor += length;
        }

        Assert.Equal(words.Length, cursor);
        Assert.True(bound > 1);
    }
}

public class ReversedZDepthTests
{
    [Fact]
    public void Setup_is_float_depth_cleared_to_far_and_tested_greater_or_equal()
    {
        Assert.Equal(VkFormat.D32Sfloat, ReversedZDepth.Format);
        Assert.Equal(0f, ReversedZDepth.ClearValue().depthStencil.depth);
        Assert.Equal(VkCompareOp.GreaterOrEqual, ReversedZDepth.CompareOp);

        VkPipelineDepthStencilStateCreateInfo state = ReversedZDepth.DepthStencilState();
        Assert.True((bool)state.depthTestEnable);
        Assert.True((bool)state.depthWriteEnable);
        Assert.Equal(VkCompareOp.GreaterOrEqual, state.depthCompareOp);
        Assert.False((bool)ReversedZDepth.DepthStencilState(write: false).depthWriteEnable);
    }

    [Fact]
    public void Attachment_clears_on_load_and_keeps_the_depth_for_the_hi_z_pass()
    {
        VkAttachmentDescription attachment = ReversedZDepth.Attachment(VkImageLayout.DepthStencilAttachmentOptimal);

        Assert.Equal(VkFormat.D32Sfloat, attachment.format);
        Assert.Equal(VkAttachmentLoadOp.Clear, attachment.loadOp);
        Assert.Equal(VkAttachmentStoreOp.Store, attachment.storeOp);
        Assert.Equal(VkImageLayout.DepthStencilAttachmentOptimal, attachment.finalLayout);
    }

    [Fact]
    public void Depth_is_one_at_the_near_plane_and_falls_toward_zero_with_distance()
    {
        Assert.Equal(1f, ReversedZDepth.NdcDepth(0.1f, 0.1f));
        Assert.Equal(1f, ReversedZDepth.NdcDepth(0.1f, 0.05f));
        float previous = 1f;
        foreach (float distance in new[] { 1f, 1e3f, 1e5f, 1e7f })
        {
            float depth = ReversedZDepth.NdcDepth(0.1f, distance);
            Assert.InRange(depth, 0f, previous);
            Assert.True(depth < previous);
            previous = depth;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ReversedZDepth.NdcDepth(0f, 1f));
    }

    [Fact]
    public void Float_depth_keeps_distinct_values_across_seven_orders_of_magnitude_that_a_forward_z_would_merge()
    {
        // Forward-Z with a float buffer: depth near 1 has spacing of about 6e-8, so distances beyond about 1e7 x near collapse.
        // Reversed-Z puts the far field near 0, where floats are dense: the values at 1e6 and 1.1e6 x near still differ.
        float a = ReversedZDepth.NdcDepth(0.1f, 1e5f);
        float b = ReversedZDepth.NdcDepth(0.1f, 1.1e5f);
        Assert.NotEqual(a, b);
        Assert.True(MathF.BitIncrement(b) < a);
    }
}
