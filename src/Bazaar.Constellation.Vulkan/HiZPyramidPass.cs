using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// GPU side of the Hi-Z depth pyramid (design doc section 11, occlusion). Owns an R32_SFLOAT pyramid image with a full mip
/// chain, one view per mip plus a whole-chain view for the consumer, the compute pipeline, and one descriptor set per
/// dispatch. <see cref="Record"/> reduces the reversed-Z depth image into mip 0 and each mip into the next with MIN over
/// the footprint, so a texel holds the farthest surface under it and the culling test stays conservative.
/// The pyramid stays in the GENERAL layout for its life; ordering between mips uses buffer-free image memory barriers.
/// Not yet run on a GPU: the benchmark thread verifies it on the Arc B580.
/// </summary>
internal sealed unsafe class HiZPyramidPass : IDisposable
{
    private const uint _pushConstantBytes = 4 * sizeof(int);

    private readonly VkInstanceApi _instanceApi;
    private readonly VkDeviceApi _deviceApi;
    private readonly VkPhysicalDevice _physicalDevice;
    private readonly VkImage _depthImage;
    private readonly VkImageView _depthView;
    private readonly uint _depthWidth;
    private readonly uint _depthHeight;
    private readonly uint _baseWidth;
    private readonly uint _baseHeight;
    private readonly uint _mipCount;
    private readonly IReadOnlyList<HiZStep> _steps;
    private readonly VkImageView[] _mipViews;
    private readonly VkDescriptorSet[] _descriptorSets;
    private VkImage _pyramid;
    private VkDeviceMemory _pyramidMemory;
    private VkImageView _chainView;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _descriptorPool;
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private bool _initialized;
    private bool _disposed;

    /// <param name="depthImage">The reversed-Z depth image (usage must include sampled); it is left in attachment layout after <see cref="Record"/>.</param>
    /// <param name="depthView">Depth-aspect view of <paramref name="depthImage"/>.</param>
    public HiZPyramidPass(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice,
        VkImage depthImage, VkImageView depthView, uint depthWidth, uint depthHeight)
    {
        _instanceApi = instanceApi;
        _deviceApi = deviceApi;
        _physicalDevice = physicalDevice;
        _depthImage = depthImage;
        _depthView = depthView;
        _depthWidth = depthWidth;
        _depthHeight = depthHeight;
        (_baseWidth, _baseHeight) = HiZPyramidPlan.BaseExtent(depthWidth, depthHeight);
        _mipCount = HiZPyramidPlan.MipCount(_baseWidth, _baseHeight);
        _steps = HiZPyramidPlan.Steps(_mipCount);
        _mipViews = new VkImageView[_mipCount];
        _descriptorSets = new VkDescriptorSet[_mipCount];
        try
        {
            CreatePyramid();
            CreateDescriptors();
            CreatePipeline();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The pyramid image, for the culling pass and tests.</summary>
    public VkImage Pyramid => _pyramid;

    /// <summary>View over every mip, for the culling pass to sample with an explicit level of detail (GENERAL layout).</summary>
    public VkImageView ChainView => _chainView;

    public uint MipCount => _mipCount;

    public uint BaseWidth => _baseWidth;

    public uint BaseHeight => _baseHeight;

    /// <summary>
    /// Records the whole build. Call after the render pass that wrote the depth image has ended, with the depth image in
    /// attachment layout; it is back in attachment layout when this returns, and the pyramid is ready for compute reads.
    /// </summary>
    public void Record(VkCommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            RecordInitialLayout(commandBuffer);
            _initialized = true;
        }

        _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _pipeline);
        foreach (HiZStep step in _steps)
        {
            switch (step.Kind)
            {
                case HiZStepKind.PyramidFromConsumer:
                    RecordMemoryBarrier(commandBuffer, VkAccessFlags.ShaderRead, VkAccessFlags.ShaderWrite);
                    break;
                case HiZStepKind.DepthToReadOnly:
                    RecordDepthBarrier(commandBuffer, toReadOnly: true);
                    break;
                case HiZStepKind.Dispatch:
                    RecordDispatch(commandBuffer, step.Mip, step.SourceMip);
                    break;
                case HiZStepKind.MipWriteToRead:
                    RecordMipBarrier(commandBuffer, (uint)step.Mip);
                    break;
                case HiZStepKind.PyramidToConsumer:
                    RecordMemoryBarrier(commandBuffer, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead);
                    break;
                case HiZStepKind.DepthToAttachment:
                    RecordDepthBarrier(commandBuffer, toReadOnly: false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Hi-Z step {step.Kind}.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pipeline.Handle != 0)
        {
            _deviceApi.vkDestroyPipeline(_pipeline, null);
        }

        if (_layout.Handle != 0)
        {
            _deviceApi.vkDestroyPipelineLayout(_layout, null);
        }

        if (_descriptorPool.Handle != 0)
        {
            _deviceApi.vkDestroyDescriptorPool(_descriptorPool, null);
        }

        if (_setLayout.Handle != 0)
        {
            _deviceApi.vkDestroyDescriptorSetLayout(_setLayout, null);
        }

        foreach (VkImageView view in _mipViews)
        {
            if (view.Handle != 0)
            {
                _deviceApi.vkDestroyImageView(view, null);
            }
        }

        if (_chainView.Handle != 0)
        {
            _deviceApi.vkDestroyImageView(_chainView, null);
        }

        if (_pyramid.Handle != 0)
        {
            _deviceApi.vkDestroyImage(_pyramid, null);
        }

        if (_pyramidMemory.Handle != 0)
        {
            _deviceApi.vkFreeMemory(_pyramidMemory, null);
        }
    }

    private void CreatePyramid()
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.R32Sfloat,
            extent = new VkExtent3D { width = _baseWidth, height = _baseHeight, depth = 1 },
            mipLevels = _mipCount,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkResult created = _deviceApi.vkCreateImage(&imageInfo, null, out _pyramid);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan Hi-Z pyramid image could not be created ({created}).");
        }

        _deviceApi.vkGetImageMemoryRequirements(_pyramid, out VkMemoryRequirements requirements);
        _pyramidMemory = VulkanMemory.AllocateDeviceLocal(_instanceApi, _deviceApi, _physicalDevice, requirements);
        _deviceApi.vkBindImageMemory(_pyramid, _pyramidMemory, 0);

        for (uint mip = 0; mip < _mipCount; mip++)
        {
            _mipViews[mip] = CreateView(mip, 1);
        }

        _chainView = CreateView(0, _mipCount);
    }

    private VkImageView CreateView(uint baseMip, uint levels)
    {
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = _pyramid,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.R32Sfloat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = baseMip,
                levelCount = levels,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };
        VkResult created = _deviceApi.vkCreateImageView(&viewInfo, null, out VkImageView view);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan Hi-Z pyramid view could not be created ({created}).");
        }

        return view;
    }

    /// <summary>Set layout: binding 0 the source as a sampled image (fetched with texelFetch, so no sampler), binding 1 the destination mip as a storage image.</summary>
    private void CreateDescriptors()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        bindings[0] = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.SampledImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Compute };
        bindings[1] = new VkDescriptorSetLayoutBinding { binding = 1, descriptorType = VkDescriptorType.StorageImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Compute };
        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = 2,
            pBindings = bindings,
        };
        VkResult layoutCreated = _deviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out _setLayout);
        if (layoutCreated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan Hi-Z descriptor layout could not be created ({layoutCreated}).");
        }

        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = _mipCount };
        poolSizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageImage, descriptorCount = _mipCount };
        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VkStructureType.DescriptorPoolCreateInfo,
            maxSets = _mipCount,
            poolSizeCount = 2,
            pPoolSizes = poolSizes,
        };
        VkResult poolCreated = _deviceApi.vkCreateDescriptorPool(&poolInfo, null, out _descriptorPool);
        if (poolCreated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan Hi-Z descriptor pool could not be created ({poolCreated}).");
        }

        var layouts = new VkDescriptorSetLayout[_mipCount];
        Array.Fill(layouts, _setLayout);
        VkResult allocated;
        fixed (VkDescriptorSetLayout* layoutPtr = layouts)
        fixed (VkDescriptorSet* setPtr = _descriptorSets)
        {
            var allocate = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.DescriptorSetAllocateInfo,
                descriptorPool = _descriptorPool,
                descriptorSetCount = _mipCount,
                pSetLayouts = layoutPtr,
            };
            allocated = _deviceApi.vkAllocateDescriptorSets(&allocate, setPtr);
        }

        if (allocated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan Hi-Z descriptor sets could not be allocated ({allocated}).");
        }

        // Set m writes mip m. Set 0 reads the depth image; set m reads mip m - 1.
        var writes = stackalloc VkWriteDescriptorSet[2];
        for (int mip = 0; mip < _mipCount; mip++)
        {
            var source = new VkDescriptorImageInfo
            {
                imageView = mip == 0 ? _depthView : _mipViews[mip - 1],
                imageLayout = mip == 0 ? VkImageLayout.DepthStencilReadOnlyOptimal : VkImageLayout.General,
            };
            var destination = new VkDescriptorImageInfo { imageView = _mipViews[mip], imageLayout = VkImageLayout.General };
            writes[0] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = _descriptorSets[mip],
                dstBinding = 0,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.SampledImage,
                pImageInfo = &source,
            };
            writes[1] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = _descriptorSets[mip],
                dstBinding = 1,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageImage,
                pImageInfo = &destination,
            };
            _deviceApi.vkUpdateDescriptorSets(2, writes, 0, null);
        }
    }

    private void CreatePipeline()
    {
        VkShaderModule module = CreateShaderModule("hiz_downsample.comp.spv");
        try
        {
            VkDescriptorSetLayout setLayout = _setLayout;
            var pushRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = _pushConstantBytes };
            var layoutInfo = new VkPipelineLayoutCreateInfo
            {
                sType = VkStructureType.PipelineLayoutCreateInfo,
                setLayoutCount = 1,
                pSetLayouts = &setLayout,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &pushRange,
            };
            VkResult layoutCreated = _deviceApi.vkCreatePipelineLayout(&layoutInfo, null, out _layout);
            if (layoutCreated != VkResult.Success)
            {
                throw new InvalidOperationException($"Vulkan Hi-Z pipeline layout could not be created ({layoutCreated}).");
            }

            ReadOnlySpan<byte> entryName = "main"u8;
            fixed (byte* entry = entryName)
            {
                var info = new VkComputePipelineCreateInfo
                {
                    sType = VkStructureType.ComputePipelineCreateInfo,
                    stage = new VkPipelineShaderStageCreateInfo
                    {
                        sType = VkStructureType.PipelineShaderStageCreateInfo,
                        stage = VkShaderStageFlags.Compute,
                        module = module,
                        pName = entry,
                    },
                    layout = _layout,
                };
                VkPipeline pipeline;
                VkResult created = _deviceApi.vkCreateComputePipelines(default, 1, &info, null, &pipeline);
                _pipeline = pipeline;
                if (created != VkResult.Success)
                {
                    throw new InvalidOperationException($"Vulkan Hi-Z pipeline could not be created ({created}).");
                }
            }
        }
        finally
        {
            _deviceApi.vkDestroyShaderModule(module, null);
        }
    }

    private VkShaderModule CreateShaderModule(string resourceName)
    {
        using Stream stream = typeof(HiZPyramidPass).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{resourceName}")
            ?? throw new InvalidOperationException($"Embedded shader {resourceName} is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] code = memory.ToArray();
        fixed (byte* codePtr = code)
        {
            var info = new VkShaderModuleCreateInfo
            {
                sType = VkStructureType.ShaderModuleCreateInfo,
                codeSize = (nuint)code.Length,
                pCode = (uint*)codePtr,
            };
            VkResult created = _deviceApi.vkCreateShaderModule(&info, null, out VkShaderModule module);
            if (created != VkResult.Success)
            {
                throw new InvalidOperationException($"Vulkan shader module {resourceName} could not be created ({created}).");
            }

            return module;
        }
    }

    /// <summary>First use: move every pyramid mip from undefined to GENERAL, where it stays.</summary>
    private void RecordInitialLayout(VkCommandBuffer commandBuffer)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = default,
            dstAccessMask = VkAccessFlags.ShaderWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _pyramid,
            subresourceRange = ColorRange(0, _mipCount),
        };
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.ComputeShader, default, 0, null, 0, null, 1, &barrier);
    }

    private void RecordDispatch(VkCommandBuffer commandBuffer, int mip, int sourceMip)
    {
        (uint dstWidth, uint dstHeight) = HiZPyramidPlan.MipExtent(_baseWidth, _baseHeight, (uint)mip);
        (uint srcWidth, uint srcHeight) = sourceMip < 0
            ? (_depthWidth, _depthHeight)
            : HiZPyramidPlan.MipExtent(_baseWidth, _baseHeight, (uint)sourceMip);
        VkDescriptorSet set = _descriptorSets[mip];
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        int* push = stackalloc int[4] { (int)srcWidth, (int)srcHeight, (int)dstWidth, (int)dstHeight };
        _deviceApi.vkCmdPushConstants(commandBuffer, _layout, VkShaderStageFlags.Compute, 0, _pushConstantBytes, push);
        _deviceApi.vkCmdDispatch(commandBuffer, HiZPyramidPlan.GroupCount(dstWidth), HiZPyramidPlan.GroupCount(dstHeight), 1);
    }

    /// <summary>Writes of one mip must be visible to the next dispatch's sampled reads of it. Same layout, so a subresource barrier carries only the memory dependency.</summary>
    private void RecordMipBarrier(VkCommandBuffer commandBuffer, uint mip)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = VkAccessFlags.ShaderWrite,
            dstAccessMask = VkAccessFlags.ShaderRead,
            oldLayout = VkImageLayout.General,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _pyramid,
            subresourceRange = ColorRange(mip, 1),
        };
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, default, 0, null, 0, null, 1, &barrier);
    }

    /// <summary>Whole-pyramid dependency between the build and its neighbours in the queue (previous consumer before, next consumer after).</summary>
    private void RecordMemoryBarrier(VkCommandBuffer commandBuffer, VkAccessFlags source, VkAccessFlags destination)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = source,
            dstAccessMask = destination,
            oldLayout = VkImageLayout.General,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _pyramid,
            subresourceRange = ColorRange(0, _mipCount),
        };
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, default, 0, null, 0, null, 1, &barrier);
    }

    private void RecordDepthBarrier(VkCommandBuffer commandBuffer, bool toReadOnly)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = toReadOnly ? VkAccessFlags.DepthStencilAttachmentWrite : VkAccessFlags.ShaderRead,
            dstAccessMask = toReadOnly
                ? VkAccessFlags.ShaderRead
                : VkAccessFlags.DepthStencilAttachmentRead | VkAccessFlags.DepthStencilAttachmentWrite,
            oldLayout = toReadOnly ? VkImageLayout.DepthStencilAttachmentOptimal : VkImageLayout.DepthStencilReadOnlyOptimal,
            newLayout = toReadOnly ? VkImageLayout.DepthStencilReadOnlyOptimal : VkImageLayout.DepthStencilAttachmentOptimal,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _depthImage,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };
        VkPipelineStageFlags attachmentStages = VkPipelineStageFlags.EarlyFragmentTests | VkPipelineStageFlags.LateFragmentTests;
        VkPipelineStageFlags source = toReadOnly ? VkPipelineStageFlags.LateFragmentTests : VkPipelineStageFlags.ComputeShader;
        VkPipelineStageFlags destination = toReadOnly ? VkPipelineStageFlags.ComputeShader : attachmentStages;
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, source, destination, default, 0, null, 0, null, 1, &barrier);
    }

    private static VkImageSubresourceRange ColorRange(uint baseMip, uint levels) => new()
    {
        aspectMask = VkImageAspectFlags.Color,
        baseMipLevel = baseMip,
        levelCount = levels,
        baseArrayLayer = 0,
        layerCount = 1,
    };
}
