using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>Offset and scale that map world space to clip space: clip = (world - offset) * scale.</summary>
public readonly record struct NodeView(float OffsetX, float OffsetY, float ScaleX, float ScaleY)
{
    /// <summary>Identity view: world coordinates already in clip space.</summary>
    public static NodeView Identity { get; } = new(0f, 0f, 1f, 1f);
}

/// <summary>
/// Graphics pipeline and buffers for drawing node instances as styled discs or rings.
/// The render pass, pipeline, descriptor set and corner buffer are built once per swapchain format. The instance
/// buffer and the style buffer grow on demand and are host-visible, so they are written with a plain copy.
/// Callers must not overwrite the instance buffer while a frame that reads it is still in flight;
/// <see cref="VulkanSwapchain"/> waits on its frame fence before uploading. Style uploads wait for the device to go idle.
/// </summary>
internal sealed unsafe class NodePipeline : IDisposable
{
    private const uint _pushConstantBytes = 4 * sizeof(float);

    private readonly VkInstanceApi _instanceApi;
    private readonly VkDeviceApi _deviceApi;
    private readonly VkPhysicalDevice _physicalDevice;
    private readonly VkFormat _format;
    private readonly VkImageLayout _finalLayout;
    private readonly bool _reversedZ;
    private VkRenderPass _renderPass;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _descriptorPool;
    private VkDescriptorSet _descriptorSet;
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private VkBuffer _cornerBuffer;
    private VkDeviceMemory _cornerMemory;
    private VkBuffer _instanceBuffer;
    private VkDeviceMemory _instanceMemory;
    private ulong _instanceCapacity;
    private void* _instanceMapped;
    private VkBuffer _styleBuffer;
    private VkDeviceMemory _styleMemory;
    private ulong _styleCapacity;
    private void* _styleMapped;
    private int _styleCount;
    private bool _disposed;

    /// <param name="finalLayout">Layout the colour target is left in after the pass: present for a swapchain, transfer-source for an offscreen copy.</param>
    /// <param name="reversedZ">
    /// Adds a reversed-Z depth attachment (<see cref="ReversedZDepth"/>) as attachment 1: the render pass clears it to 0, the pipeline tests
    /// greater-or-equal. Framebuffers must then carry a depth view second. Off by default: the 2D path has no depth.
    /// </param>
    public NodePipeline(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice, VkFormat format, VkImageLayout finalLayout, bool reversedZ = false)
    {
        _instanceApi = instanceApi;
        _deviceApi = deviceApi;
        _physicalDevice = physicalDevice;
        _format = format;
        _finalLayout = finalLayout;
        _reversedZ = reversedZ;
        CreateRenderPass();
        CreateDescriptors();
        CreatePipeline();
        CreateCornerBuffer();
        ResizeStyleBuffer(NodeStylePacking.StyleStride);
    }

    public VkRenderPass RenderPass => _renderPass;

    /// <summary>Number of styles uploaded by the last <see cref="UploadStyles"/>. Nodes are drawn only when this is above zero.</summary>
    public int StyleCount => _styleCount;

    /// <summary>Copies packed instance bytes into the instance buffer, growing it when needed.</summary>
    public void Upload(ReadOnlySpan<byte> instanceBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (instanceBytes.IsEmpty)
        {
            return;
        }

        if ((ulong)instanceBytes.Length > _instanceCapacity)
        {
            ResizeInstanceBuffer((ulong)instanceBytes.Length);
        }

        instanceBytes.CopyTo(new Span<byte>(_instanceMapped, instanceBytes.Length));
    }

    /// <summary>
    /// Replaces the style table with packed bytes from <see cref="NodeStylePacking"/>. Waits for the device to go idle,
    /// so it is safe between frames. Empty bytes clear the table, and nodes are then not drawn.
    /// </summary>
    public void UploadStyles(ReadOnlySpan<byte> styleBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (styleBytes.Length % NodeStylePacking.StyleStride != 0)
        {
            throw new ArgumentException($"Style bytes must be a multiple of {NodeStylePacking.StyleStride}.", nameof(styleBytes));
        }

        if (styleBytes.IsEmpty)
        {
            _styleCount = 0;
            return;
        }

        _deviceApi.vkDeviceWaitIdle();
        if ((ulong)styleBytes.Length > _styleCapacity)
        {
            ResizeStyleBuffer((ulong)styleBytes.Length);
        }

        styleBytes.CopyTo(new Span<byte>(_styleMapped, styleBytes.Length));
        _styleCount = styleBytes.Length / NodeStylePacking.StyleStride;
    }

    /// <summary>Records one render pass: clear, then draw the instances when there are any and styles are uploaded.</summary>
    public void Record(VkCommandBuffer commandBuffer, VkFramebuffer framebuffer, VkExtent2D extent,
        (float R, float G, float B, float A) clear, int instanceCount, NodeView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clearValues = stackalloc VkClearValue[2];
        clearValues[0] = new VkClearValue();
        clearValues[0].color.float32[0] = clear.R;
        clearValues[0].color.float32[1] = clear.G;
        clearValues[0].color.float32[2] = clear.B;
        clearValues[0].color.float32[3] = clear.A;
        clearValues[1] = ReversedZDepth.ClearValue();

        var begin = new VkRenderPassBeginInfo
        {
            sType = VkStructureType.RenderPassBeginInfo,
            renderPass = _renderPass,
            framebuffer = framebuffer,
            renderArea = new VkRect2D { offset = new VkOffset2D(0, 0), extent = extent },
            clearValueCount = _reversedZ ? 2u : 1u,
            pClearValues = clearValues,
        };
        _deviceApi.vkCmdBeginRenderPass(commandBuffer, &begin, VkSubpassContents.Inline);

        if (instanceCount > 0 && _styleCount > 0)
        {
            var viewport = new VkViewport
            {
                x = 0f,
                y = 0f,
                width = extent.width,
                height = extent.height,
                minDepth = 0f,
                maxDepth = 1f,
            };
            var scissor = new VkRect2D { offset = new VkOffset2D(0, 0), extent = extent };
            _deviceApi.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
            _deviceApi.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
            _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);

            VkDescriptorSet descriptorSet = _descriptorSet;
            _deviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics, _layout, 0, 1, &descriptorSet, 0, null);

            VkBuffer* buffers = stackalloc VkBuffer[2] { _cornerBuffer, _instanceBuffer };
            ulong* offsets = stackalloc ulong[2] { 0, 0 };
            _deviceApi.vkCmdBindVertexBuffers(commandBuffer, 0, 2, buffers, offsets);

            float* push = stackalloc float[4] { view.OffsetX, view.OffsetY, view.ScaleX, view.ScaleY };
            _deviceApi.vkCmdPushConstants(commandBuffer, _layout, VkShaderStageFlags.Vertex, 0, _pushConstantBytes, push);
            _deviceApi.vkCmdDraw(commandBuffer, 4, (uint)instanceCount, 0, 0);
        }

        _deviceApi.vkCmdEndRenderPass(commandBuffer);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_instanceMapped is not null)
        {
            _deviceApi.vkUnmapMemory(_instanceMemory);
            _instanceMapped = null;
        }

        if (_styleMapped is not null)
        {
            _deviceApi.vkUnmapMemory(_styleMemory);
            _styleMapped = null;
        }

        DestroyBuffer(ref _styleBuffer, ref _styleMemory);
        DestroyBuffer(ref _instanceBuffer, ref _instanceMemory);
        DestroyBuffer(ref _cornerBuffer, ref _cornerMemory);
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

        if (_renderPass.Handle != 0)
        {
            _deviceApi.vkDestroyRenderPass(_renderPass, null);
        }
    }

    private void CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            format = _format,
            samples = VkSampleCountFlags.Count1,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare,
            stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined,
            finalLayout = _finalLayout,
        };
        var colorRef = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference { attachment = 1, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var attachments = stackalloc VkAttachmentDescription[2];
        attachments[0] = attachment;
        attachments[1] = ReversedZDepth.Attachment(VkImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
            pDepthStencilAttachment = _reversedZ ? &depthRef : null,
        };
        var dependency = new VkSubpassDependency
        {
            srcSubpass = VK_SUBPASS_EXTERNAL,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput | (_reversedZ ? VkPipelineStageFlags.EarlyFragmentTests | VkPipelineStageFlags.LateFragmentTests : default),
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput | (_reversedZ ? VkPipelineStageFlags.EarlyFragmentTests | VkPipelineStageFlags.LateFragmentTests : default),
            srcAccessMask = default,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite | (_reversedZ ? VkAccessFlags.DepthStencilAttachmentWrite : default),
        };
        var info = new VkRenderPassCreateInfo
        {
            sType = VkStructureType.RenderPassCreateInfo,
            attachmentCount = _reversedZ ? 2u : 1u,
            pAttachments = attachments,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 1,
            pDependencies = &dependency,
        };
        VkResult created = _deviceApi.vkCreateRenderPass(&info, null, out _renderPass);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan render pass could not be created ({created}).");
        }
    }

    /// <summary>One storage buffer at set 0, binding 0, read by both shader stages; the pool holds one set for it.</summary>
    private void CreateDescriptors()
    {
        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.StorageBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
        };
        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = 1,
            pBindings = &binding,
        };
        VkResult layoutCreated = _deviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out _setLayout);
        if (layoutCreated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan style descriptor layout could not be created ({layoutCreated}).");
        }

        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VkStructureType.DescriptorPoolCreateInfo,
            maxSets = 1,
            poolSizeCount = 1,
            pPoolSizes = &poolSize,
        };
        VkResult poolCreated = _deviceApi.vkCreateDescriptorPool(&poolInfo, null, out _descriptorPool);
        if (poolCreated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan descriptor pool could not be created ({poolCreated}).");
        }

        VkDescriptorSetLayout setLayout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = _descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &setLayout,
        };
        VkResult allocated;
        fixed (VkDescriptorSet* sets = &_descriptorSet)
        {
            allocated = _deviceApi.vkAllocateDescriptorSets(&allocate, sets);
        }

        if (allocated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan descriptor set could not be allocated ({allocated}).");
        }
    }

    private void CreatePipeline()
    {
        VkShaderModule vert = CreateShaderModule("node.vert.spv");
        VkShaderModule frag = CreateShaderModule("node.frag.spv");
        try
        {
            VkDescriptorSetLayout setLayout = _setLayout;
            var pushRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Vertex, offset = 0, size = _pushConstantBytes };
            var layoutInfo = new VkPipelineLayoutCreateInfo
            {
                sType = VkStructureType.PipelineLayoutCreateInfo,
                setLayoutCount = 1,
                pSetLayouts = &setLayout,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &pushRange,
            };
            _deviceApi.vkCreatePipelineLayout(&layoutInfo, null, out _layout);

            VkVertexInputBindingDescription[] bindings = NodeInstanceVertexLayout.Bindings;
            VkVertexInputAttributeDescription[] attributes = NodeInstanceVertexLayout.Attributes;
            ReadOnlySpan<byte> entryName = "main"u8;
            fixed (byte* entry = entryName)
            fixed (VkVertexInputBindingDescription* bindingPtr = bindings)
            fixed (VkVertexInputAttributeDescription* attributePtr = attributes)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
                stages[0] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Vertex, module = vert, pName = entry };
                stages[1] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Fragment, module = frag, pName = entry };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    sType = VkStructureType.PipelineVertexInputStateCreateInfo,
                    vertexBindingDescriptionCount = (uint)bindings.Length,
                    pVertexBindingDescriptions = bindingPtr,
                    vertexAttributeDescriptionCount = (uint)attributes.Length,
                    pVertexAttributeDescriptions = attributePtr,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo
                {
                    sType = VkStructureType.PipelineInputAssemblyStateCreateInfo,
                    topology = VkPrimitiveTopology.TriangleStrip,
                };
                var viewport = new VkPipelineViewportStateCreateInfo
                {
                    sType = VkStructureType.PipelineViewportStateCreateInfo,
                    viewportCount = 1,
                    scissorCount = 1,
                };
                var rasterization = new VkPipelineRasterizationStateCreateInfo
                {
                    sType = VkStructureType.PipelineRasterizationStateCreateInfo,
                    polygonMode = VkPolygonMode.Fill,
                    cullMode = VkCullModeFlags.None,
                    frontFace = VkFrontFace.CounterClockwise,
                    lineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo
                {
                    sType = VkStructureType.PipelineMultisampleStateCreateInfo,
                    rasterizationSamples = VkSampleCountFlags.Count1,
                };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    blendEnable = true,
                    srcColorBlendFactor = VkBlendFactor.SrcAlpha,
                    dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    colorBlendOp = VkBlendOp.Add,
                    srcAlphaBlendFactor = VkBlendFactor.One,
                    dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    alphaBlendOp = VkBlendOp.Add,
                    colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo
                {
                    sType = VkStructureType.PipelineColorBlendStateCreateInfo,
                    attachmentCount = 1,
                    pAttachments = &blendAttachment,
                };
                VkPipelineDepthStencilStateCreateInfo depthStencil = ReversedZDepth.DepthStencilState();
                var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo
                {
                    sType = VkStructureType.PipelineDynamicStateCreateInfo,
                    dynamicStateCount = 2,
                    pDynamicStates = dynamicStates,
                };

                var info = new VkGraphicsPipelineCreateInfo
                {
                    sType = VkStructureType.GraphicsPipelineCreateInfo,
                    stageCount = 2,
                    pStages = stages,
                    pVertexInputState = &vertexInput,
                    pInputAssemblyState = &inputAssembly,
                    pViewportState = &viewport,
                    pRasterizationState = &rasterization,
                    pMultisampleState = &multisample,
                    pColorBlendState = &blend,
                    pDepthStencilState = _reversedZ ? &depthStencil : null,
                    pDynamicState = &dynamic,
                    layout = _layout,
                    renderPass = _renderPass,
                    subpass = 0,
                };
                VkPipeline pipeline;
                VkResult created = _deviceApi.vkCreateGraphicsPipelines(default, 1, &info, null, &pipeline);
                _pipeline = pipeline;
                if (created != VkResult.Success)
                {
                    throw new InvalidOperationException($"Vulkan node pipeline could not be created ({created}).");
                }
            }
        }
        finally
        {
            _deviceApi.vkDestroyShaderModule(vert, null);
            _deviceApi.vkDestroyShaderModule(frag, null);
        }
    }

    private VkShaderModule CreateShaderModule(string resourceName)
    {
        byte[] code = LoadShader(resourceName);
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

    private static byte[] LoadShader(string name)
    {
        using Stream stream = typeof(NodePipeline).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{name}")
            ?? throw new InvalidOperationException($"Embedded shader {name} is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void CreateCornerBuffer()
    {
        ReadOnlySpan<float> corners = NodeInstanceVertexLayout.QuadCorners;
        ulong bytes = (ulong)(corners.Length * sizeof(float));
        (_cornerBuffer, _cornerMemory) = CreateHostBuffer(bytes, VkBufferUsageFlags.VertexBuffer);
        void* mapped = Map(_cornerMemory, bytes);
        corners.CopyTo(new Span<float>(mapped, corners.Length));
        _deviceApi.vkUnmapMemory(_cornerMemory);
    }

    private void ResizeInstanceBuffer(ulong bytes)
    {
        ulong capacity = Math.Max(bytes, Math.Max(_instanceCapacity * 2, 4096UL));
        if (_instanceMapped is not null)
        {
            _deviceApi.vkUnmapMemory(_instanceMemory);
            _instanceMapped = null;
        }

        DestroyBuffer(ref _instanceBuffer, ref _instanceMemory);
        (_instanceBuffer, _instanceMemory) = CreateHostBuffer(capacity, VkBufferUsageFlags.VertexBuffer);
        _instanceMapped = Map(_instanceMemory, capacity);
        _instanceCapacity = capacity;
    }

    /// <summary>Grows the style buffer and points the descriptor set at it. Called only when the device is idle.</summary>
    private void ResizeStyleBuffer(ulong bytes)
    {
        ulong capacity = Math.Max(bytes, Math.Max(_styleCapacity * 2, (ulong)NodeStylePacking.StyleStride));
        if (_styleMapped is not null)
        {
            _deviceApi.vkUnmapMemory(_styleMemory);
            _styleMapped = null;
        }

        DestroyBuffer(ref _styleBuffer, ref _styleMemory);
        (_styleBuffer, _styleMemory) = CreateHostBuffer(capacity, VkBufferUsageFlags.StorageBuffer);
        _styleMapped = Map(_styleMemory, capacity);
        _styleCapacity = capacity;
        WriteStyleDescriptor();
    }

    private void WriteStyleDescriptor()
    {
        var bufferInfo = new VkDescriptorBufferInfo
        {
            buffer = _styleBuffer,
            offset = 0,
            range = _styleCapacity,
        };
        var write = new VkWriteDescriptorSet
        {
            sType = VkStructureType.WriteDescriptorSet,
            dstSet = _descriptorSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageBuffer,
            pBufferInfo = &bufferInfo,
        };
        _deviceApi.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void* Map(VkDeviceMemory memory, ulong size)
    {
        void* data;
        VkResult mapped = _deviceApi.vkMapMemory(memory, 0, size, VkMemoryMapFlags.None, &data);
        if (mapped != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan could not map a node buffer ({mapped}).");
        }

        return data;
    }

    private (VkBuffer Buffer, VkDeviceMemory Memory) CreateHostBuffer(ulong size, VkBufferUsageFlags usage)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
        };
        _deviceApi.vkCreateBuffer(&info, null, out VkBuffer buffer);
        _deviceApi.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        var allocate = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.MemoryAllocateInfo,
            allocationSize = requirements.size,
            memoryTypeIndex = FindHostMemoryType(requirements.memoryTypeBits),
        };
        _deviceApi.vkAllocateMemory(&allocate, null, out VkDeviceMemory memory);
        _deviceApi.vkBindBufferMemory(buffer, memory, 0);
        return (buffer, memory);
    }

    private uint FindHostMemoryType(uint allowedBits)
    {
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(_physicalDevice, out VkPhysicalDeviceMemoryProperties properties);
        const VkMemoryPropertyFlags wanted = VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent;
        for (uint index = 0; index < properties.memoryTypeCount; index++)
        {
            bool allowed = (allowedBits & (1u << (int)index)) != 0;
            if (allowed && (properties.memoryTypes[(int)index].propertyFlags & wanted) == wanted)
            {
                return index;
            }
        }

        throw new InvalidOperationException("Vulkan device has no host-visible, host-coherent memory for node buffers.");
    }

    private void DestroyBuffer(ref VkBuffer buffer, ref VkDeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            _deviceApi.vkDestroyBuffer(buffer, null);
            buffer = default;
        }

        if (memory.Handle != 0)
        {
            _deviceApi.vkFreeMemory(memory, null);
            memory = default;
        }
    }
}
