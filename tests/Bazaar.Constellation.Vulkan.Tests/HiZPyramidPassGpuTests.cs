using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// Runs <see cref="HiZPyramidPass"/> on the physical GPU against known depth images and compares every pyramid mip with the CPU
/// reference (<see cref="HiZPyramid"/>, reversed-Z, conservative MIN), texel for texel, for power-of-two and odd sizes (the reference
/// follows the GPU rule: ceil size for the first level, floor size and a 3-wide last texel on odd sources after that). Odd sizes also
/// check that every mip keeps the global minimum and that the 1x1 mip equals it. The test uses the offscreen renderer's device with the validation layer on and
/// fails on any validation error. It skips where the loader finds no usable device.
/// </summary>
public unsafe class HiZPyramidPassGpuTests
{
    private static VulkanOffscreenRenderer CreateOrSkip()
    {
        try
        {
            return VulkanOffscreenRenderer.Create(16, 16, enableValidation: true);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip(ex.Message);
            throw;
        }
    }

    public static TheoryData<int, int> PowerOfTwoSizes => new() { { 64, 32 }, { 128, 128 }, { 256, 64 } };

    public static TheoryData<int, int> OddSizes => new() { { 37, 21 }, { 129, 65 }, { 1, 1 }, { 5, 1 }, { 1, 9 }, { 100, 75 } };

    [Theory]
    [MemberData(nameof(PowerOfTwoSizes))]
    public void Power_of_two_depth_images_match_the_cpu_reference_texel_for_texel(int width, int height)
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip();
        float[] depth = MakeDepth(width, height, seed: 7);
        List<float[]> gpu = BuildOnGpu(renderer, depth, width, height, out (int W, int H)[] sizes);
        var reference = new HiZPyramid(depth, width, height);

        // Pyramid mip 0 is half the depth image, which is the reference's level 1.
        Assert.Equal(reference.LevelCount - 1, gpu.Count);
        for (int mip = 0; mip < gpu.Count; mip++)
        {
            int level = mip + 1;
            Assert.Equal((reference.LevelWidth(level), reference.LevelHeight(level)), sizes[mip]);
            for (int y = 0; y < sizes[mip].H; y++)
            {
                for (int x = 0; x < sizes[mip].W; x++)
                {
                    Assert.True(
                        reference.Get(level, x, y) == gpu[mip][(y * sizes[mip].W) + x],
                        $"{width}x{height} mip {mip} texel ({x},{y}): GPU {gpu[mip][(y * sizes[mip].W) + x]} != reference {reference.Get(level, x, y)}");
                }
            }
        }

        AssertNoValidationErrors(renderer);
    }

    [Theory]
    [MemberData(nameof(OddSizes))]
    public void Odd_depth_images_match_the_cpu_reference_texel_for_texel_and_keep_the_global_minimum(int width, int height)
    {
        using VulkanOffscreenRenderer renderer = CreateOrSkip();
        float[] depth = MakeDepth(width, height, seed: 11);
        List<float[]> gpu = BuildOnGpu(renderer, depth, width, height, out (int W, int H)[] sizes);
        var reference = new HiZPyramid(depth, width, height);
        float globalMin = depth.Min();
        if (reference.LevelCount == 1)
        {
            // A 1x1 depth image has no coarser reference level; the GPU pyramid still has its single 1x1 mip, which is the texel itself.
            Assert.Single(gpu);
            Assert.Equal(depth[0], gpu[0][0]);
            AssertNoValidationErrors(renderer);
            return;
        }

        Assert.Equal(reference.LevelCount - 1, gpu.Count);
        for (int mip = 0; mip < gpu.Count; mip++)
        {
            int level = mip + 1;
            Assert.Equal((reference.LevelWidth(level), reference.LevelHeight(level)), sizes[mip]);
            for (int y = 0; y < sizes[mip].H; y++)
            {
                for (int x = 0; x < sizes[mip].W; x++)
                {
                    float actual = gpu[mip][(y * sizes[mip].W) + x];
                    Assert.True(reference.Get(level, x, y) == actual, $"{width}x{height} mip {mip} texel ({x},{y}): GPU {actual} != reference {reference.Get(level, x, y)}");
                }
            }

            Assert.Equal(globalMin, gpu[mip].Min());
        }

        Assert.Single(gpu[^1]);
        Assert.Equal(globalMin, gpu[^1][0]);
        AssertNoValidationErrors(renderer);
    }

    private static void AssertNoValidationErrors(VulkanOffscreenRenderer renderer)
    {
        IReadOnlyList<VulkanValidationMessage> messages = VulkanOffscreenRenderer.DrainValidationMessages();
        if (!renderer.ValidationLayerEnabled)
        {
            return;
        }

        Assert.DoesNotContain(messages, m => m.Severity == "error");
    }

    /// <summary>Depth in (0, 1]: pseudo-random, with a few zeros (nothing drawn) so the MIN rule is exercised.</summary>
    private static float[] MakeDepth(int width, int height, int seed)
    {
        var random = new Random(seed);
        var depth = new float[width * height];
        for (int i = 0; i < depth.Length; i++)
        {
            depth[i] = random.Next(0, 20) == 0 ? 0f : (float)((random.NextDouble() * 0.999) + 0.001);
        }

        return depth;
    }

    /// <summary>Uploads <paramref name="depth"/> into a D32 image, runs the pass, and reads every mip back as floats.</summary>
    private static List<float[]> BuildOnGpu(VulkanOffscreenRenderer renderer, float[] depth, int width, int height, out (int W, int H)[] sizes)
    {
        VkInstanceApi instanceApi = renderer.InstanceApi;
        VkDeviceApi deviceApi = renderer.DeviceApi;
        VkPhysicalDevice physical = renderer.PhysicalDevice;
        instanceApi.vkGetPhysicalDeviceMemoryProperties(physical, out VkPhysicalDeviceMemoryProperties memory);

        // Depth image that can be filled by a buffer copy and then read as a sampled depth image.
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = ReversedZDepth.Format,
            extent = new VkExtent3D { width = (uint)width, height = (uint)height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        Check(deviceApi.vkCreateImage(&imageInfo, null, out VkImage depthImage), "vkCreateImage(depth)");
        deviceApi.vkGetImageMemoryRequirements(depthImage, out VkMemoryRequirements depthRequirements);
        VkDeviceMemory depthMemory = Allocate(deviceApi, memory, depthRequirements, VkMemoryPropertyFlags.DeviceLocal);
        Check(deviceApi.vkBindImageMemory(depthImage, depthMemory, 0), "vkBindImageMemory(depth)");
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = depthImage,
            viewType = VkImageViewType.Image2D,
            format = ReversedZDepth.Format,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Depth, baseMipLevel = 0, levelCount = 1, baseArrayLayer = 0, layerCount = 1 },
        };
        Check(deviceApi.vkCreateImageView(&viewInfo, null, out VkImageView depthView), "vkCreateImageView(depth)");

        // Staging buffer with the depth values.
        ulong depthBytes = (ulong)depth.Length * sizeof(float);
        VkBuffer staging = CreateBuffer(deviceApi, depthBytes, VkBufferUsageFlags.TransferSrc, memory, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out VkDeviceMemory stagingMemory);
        void* mapped;
        Check(deviceApi.vkMapMemory(stagingMemory, 0, depthBytes, 0, &mapped), "vkMapMemory(staging)");
        depth.AsSpan().CopyTo(new Span<float>(mapped, depth.Length));
        deviceApi.vkUnmapMemory(stagingMemory);

        using var pass = new HiZPyramidPass(instanceApi, deviceApi, physical, depthImage, depthView, (uint)width, (uint)height);
        sizes = new (int, int)[pass.MipCount];
        ulong totalBytes = 0;
        var offsets = new ulong[pass.MipCount];
        for (uint mip = 0; mip < pass.MipCount; mip++)
        {
            (uint mw, uint mh) = HiZPyramidPlan.MipExtent(pass.BaseWidth, pass.BaseHeight, mip);
            sizes[mip] = ((int)mw, (int)mh);
            offsets[mip] = totalBytes;
            totalBytes += (ulong)mw * mh * sizeof(float);
        }

        VkBuffer readback = CreateBuffer(deviceApi, totalBytes, VkBufferUsageFlags.TransferDst, memory, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out VkDeviceMemory readbackMemory);

        VkCommandBuffer commandBuffer;
        VkCommandPool pool = renderer.CommandPool;
        var allocate = new VkCommandBufferAllocateInfo { sType = VkStructureType.CommandBufferAllocateInfo, commandPool = pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        Check(deviceApi.vkAllocateCommandBuffers(&allocate, &commandBuffer), "vkAllocateCommandBuffers");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(deviceApi.vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer");

        var depthRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Depth, baseMipLevel = 0, levelCount = 1, baseArrayLayer = 0, layerCount = 1 };
        ImageBarrier(deviceApi, commandBuffer, depthImage, depthRange, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, default, VkAccessFlags.TransferWrite,
            VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer);
        var copy = new VkBufferImageCopy
        {
            bufferOffset = 0,
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Depth, mipLevel = 0, baseArrayLayer = 0, layerCount = 1 },
            imageExtent = new VkExtent3D { width = (uint)width, height = (uint)height, depth = 1 },
        };
        deviceApi.vkCmdCopyBufferToImage(commandBuffer, staging, depthImage, VkImageLayout.TransferDstOptimal, 1, &copy);
        // The pass expects the depth image in attachment layout, as left by the render pass that wrote it.
        ImageBarrier(deviceApi, commandBuffer, depthImage, depthRange, VkImageLayout.TransferDstOptimal, VkImageLayout.DepthStencilAttachmentOptimal,
            VkAccessFlags.TransferWrite, VkAccessFlags.DepthStencilAttachmentWrite, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.LateFragmentTests);

        pass.Record(commandBuffer);

        // Pyramid is in GENERAL; make the compute writes visible to the transfer reads, then copy each mip out.
        var memoryBarrier = new VkMemoryBarrier { sType = VkStructureType.MemoryBarrier, srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.TransferRead };
        deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, default, 1, &memoryBarrier, 0, null, 0, null);
        for (uint mip = 0; mip < pass.MipCount; mip++)
        {
            var region = new VkBufferImageCopy
            {
                bufferOffset = offsets[mip],
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, mipLevel = mip, baseArrayLayer = 0, layerCount = 1 },
                imageExtent = new VkExtent3D { width = (uint)sizes[mip].W, height = (uint)sizes[mip].H, depth = 1 },
            };
            deviceApi.vkCmdCopyImageToBuffer(commandBuffer, pass.Pyramid, VkImageLayout.General, readback, 1, &region);
        }

        Check(deviceApi.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
        Check(deviceApi.vkCreateFence(&fenceInfo, null, out VkFence fence), "vkCreateFence");
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        Check(deviceApi.vkQueueSubmit(renderer.Queue, 1, &submit, fence), "vkQueueSubmit");
        Check(deviceApi.vkWaitForFences(1, &fence, true, 20_000_000_000), "vkWaitForFences");

        void* readMapped;
        Check(deviceApi.vkMapMemory(readbackMemory, 0, totalBytes, 0, &readMapped), "vkMapMemory(readback)");
        var mips = new List<float[]>();
        for (int mip = 0; mip < sizes.Length; mip++)
        {
            var values = new float[sizes[mip].W * sizes[mip].H];
            new ReadOnlySpan<float>((byte*)readMapped + offsets[mip], values.Length).CopyTo(values);
            mips.Add(values);
        }

        deviceApi.vkUnmapMemory(readbackMemory);
        deviceApi.vkDestroyFence(fence, null);
        deviceApi.vkFreeCommandBuffers(pool, 1, &commandBuffer);
        deviceApi.vkDestroyBuffer(readback, null);
        deviceApi.vkFreeMemory(readbackMemory, null);
        deviceApi.vkDestroyBuffer(staging, null);
        deviceApi.vkFreeMemory(stagingMemory, null);
        deviceApi.vkDestroyImageView(depthView, null);
        deviceApi.vkDestroyImage(depthImage, null);
        deviceApi.vkFreeMemory(depthMemory, null);
        return mips;
    }

    private static void ImageBarrier(VkDeviceApi deviceApi, VkCommandBuffer commandBuffer, VkImage image, VkImageSubresourceRange range,
        VkImageLayout oldLayout, VkImageLayout newLayout, VkAccessFlags source, VkAccessFlags destination, VkPipelineStageFlags sourceStage, VkPipelineStageFlags destinationStage)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = source,
            dstAccessMask = destination,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = ~0u,
            dstQueueFamilyIndex = ~0u,
            image = image,
            subresourceRange = range,
        };
        deviceApi.vkCmdPipelineBarrier(commandBuffer, sourceStage, destinationStage, default, 0, null, 0, null, 1, &barrier);
    }

    private static VkBuffer CreateBuffer(VkDeviceApi deviceApi, ulong size, VkBufferUsageFlags usage, VkPhysicalDeviceMemoryProperties memory, VkMemoryPropertyFlags flags, out VkDeviceMemory deviceMemory)
    {
        var info = new VkBufferCreateInfo { sType = VkStructureType.BufferCreateInfo, size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        Check(deviceApi.vkCreateBuffer(&info, null, out VkBuffer buffer), "vkCreateBuffer");
        deviceApi.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        deviceMemory = Allocate(deviceApi, memory, requirements, flags);
        Check(deviceApi.vkBindBufferMemory(buffer, deviceMemory, 0), "vkBindBufferMemory");
        return buffer;
    }

    private static VkDeviceMemory Allocate(VkDeviceApi deviceApi, VkPhysicalDeviceMemoryProperties memory, VkMemoryRequirements requirements, VkMemoryPropertyFlags flags)
    {
        for (uint index = 0; index < memory.memoryTypeCount; index++)
        {
            bool allowed = (requirements.memoryTypeBits & (1u << (int)index)) != 0;
            if (allowed && (memory.memoryTypes[(int)index].propertyFlags & flags) == flags)
            {
                var allocate = new VkMemoryAllocateInfo { sType = VkStructureType.MemoryAllocateInfo, allocationSize = requirements.size, memoryTypeIndex = index };
                Check(deviceApi.vkAllocateMemory(&allocate, null, out VkDeviceMemory result), "vkAllocateMemory");
                return result;
            }
        }

        throw new InvalidOperationException($"No memory type matches {flags}.");
    }

    private static void Check(VkResult result, string call)
    {
        Assert.True(result == VkResult.Success, $"{call} failed with {result}.");
    }
}
