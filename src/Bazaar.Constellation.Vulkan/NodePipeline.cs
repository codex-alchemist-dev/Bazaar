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
/// Graphics pipeline and buffers for drawing node instances as instanced discs.
/// The render pass, pipeline and corner buffer are built once per swapchain format. The instance buffer grows
/// on demand and is host-visible, so it is written with a plain copy. Callers must not overwrite it while a frame
/// that reads it is still in flight; <see cref="VulkanSwapchain"/> waits on its frame fence before uploading.
/// </summary>
internal sealed unsafe class NodePipeline : IDisposable
{
    private const uint _pushConstantBytes = 4 * sizeof(float);

    private readonly VkInstanceApi _instanceApi;
    private readonly VkDeviceApi _deviceApi;
    private readonly VkPhysicalDevice _physicalDevice;
    private readonly VkFormat _format;
    private readonly VkImageLayout _finalLayout;
    private VkRenderPass _renderPass;
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private VkBuffer _cornerBuffer;
    private VkDeviceMemory _cornerMemory;
    private VkBuffer _instanceBuffer;
    private VkDeviceMemory _instanceMemory;
    private ulong _instanceCapacity;
    private void* _instanceMapped;
    private bool _disposed;

    /// <param name="finalLayout">Layout the colour target is left in after the pass: present for a swapchain, transfer-source for an offscreen copy.</param>
    public NodePipeline(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice, VkFormat format, VkImageLayout finalLayout)
    {
        _instanceApi = instanceApi;
        _deviceApi = deviceApi;
        _physicalDevice = physicalDevice;
        _format = format;
        _finalLayout = finalLayout;
        CreateRenderPass();
        CreatePipeline();
        CreateCornerBuffer();
    }

    public VkRenderPass RenderPass => _renderPass;

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

    /// <summary>Records one render pass: clear, then draw the instances when there are any.</summary>
    public void Record(VkCommandBuffer commandBuffer, VkFramebuffer framebuffer, VkExtent2D extent,
        (float R, float G, float B, float A) clear, int instanceCount, NodeView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clearValue = new VkClearValue();
        clearValue.color.float32[0] = clear.R;
        clearValue.color.float32[1] = clear.G;
        clearValue.color.float32[2] = clear.B;
        clearValue.color.float32[3] = clear.A;

        var begin = new VkRenderPassBeginInfo
        {
            sType = VkStructureType.RenderPassBeginInfo,
            renderPass = _renderPass,
            framebuffer = framebuffer,
            renderArea = new VkRect2D { offset = new VkOffset2D(0, 0), extent = extent },
            clearValueCount = 1,
            pClearValues = &clearValue,
        };
        _deviceApi.vkCmdBeginRenderPass(commandBuffer, &begin, VkSubpassContents.Inline);

        if (instanceCount > 0)
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
        var subpass = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
        };
        var dependency = new VkSubpassDependency
        {
            srcSubpass = VK_SUBPASS_EXTERNAL,
            dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput,
            srcAccessMask = default,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite,
        };
        var info = new VkRenderPassCreateInfo
        {
            sType = VkStructureType.RenderPassCreateInfo,
            attachmentCount = 1,
            pAttachments = &attachment,
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

    private void CreatePipeline()
    {
        VkShaderModule vert = CreateShaderModule("node.vert.spv");
        VkShaderModule frag = CreateShaderModule("node.frag.spv");
        try
        {
            var pushRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Vertex, offset = 0, size = _pushConstantBytes };
            var layoutInfo = new VkPipelineLayoutCreateInfo
            {
                sType = VkStructureType.PipelineLayoutCreateInfo,
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
        (_cornerBuffer, _cornerMemory) = CreateHostBuffer(bytes);
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
        (_instanceBuffer, _instanceMemory) = CreateHostBuffer(capacity);
        _instanceMapped = Map(_instanceMemory, capacity);
        _instanceCapacity = capacity;
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

    private (VkBuffer Buffer, VkDeviceMemory Memory) CreateHostBuffer(ulong size)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = VkBufferUsageFlags.VertexBuffer,
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
