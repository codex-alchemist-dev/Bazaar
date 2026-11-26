using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Draws section rectangles as alpha-blended filled quads, one instance per section, inside the node render pass before the nodes.
/// No depth write and no depth test, so sections never hide anything: a node drawn afterwards always lands on top. The pipeline
/// is built against the node pipeline's render pass. Buffers are host-visible; callers upload only while no frame that reads them is
/// in flight (the swapchain waits on its frame fence first). Not thread safe.
/// </summary>
internal sealed unsafe class SectionPass : IDisposable
{
    private const uint _pushConstantBytes = 4 * sizeof(float);

    private readonly VkInstanceApi _instanceApi;
    private readonly VkDeviceApi _deviceApi;
    private readonly VkPhysicalDevice _physicalDevice;
    private readonly VkRenderPass _renderPass;
    private readonly bool _depthAttachment;
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private VkBuffer _cornerBuffer;
    private VkDeviceMemory _cornerMemory;
    private VkBuffer _instanceBuffer;
    private VkDeviceMemory _instanceMemory;
    private ulong _instanceCapacity;
    private void* _instanceMapped;
    private int _count;
    private bool _disposed;

    /// <param name="depthAttachment">True when the render pass carries a depth attachment; the pipeline then needs a depth state with test and write off.</param>
    public SectionPass(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice, VkRenderPass renderPass, bool depthAttachment)
    {
        _instanceApi = instanceApi;
        _deviceApi = deviceApi;
        _physicalDevice = physicalDevice;
        _renderPass = renderPass;
        _depthAttachment = depthAttachment;
        CreatePipeline();
        CreateCornerBuffer();
    }

    /// <summary>Number of sections uploaded by the last <see cref="Upload"/>.</summary>
    public int Count => _count;

    /// <summary>Copies packed bytes from <see cref="SectionPacking"/> into the instance buffer, growing it when needed. Empty bytes clear the set.</summary>
    public void Upload(ReadOnlySpan<byte> sectionBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sectionBytes.Length % SectionPacking.SectionStride != 0)
        {
            throw new ArgumentException($"Section bytes must be a multiple of {SectionPacking.SectionStride}.", nameof(sectionBytes));
        }

        _count = sectionBytes.Length / SectionPacking.SectionStride;
        if (_count == 0)
        {
            return;
        }

        if ((ulong)sectionBytes.Length > _instanceCapacity)
        {
            ResizeInstanceBuffer((ulong)sectionBytes.Length);
        }

        sectionBytes.CopyTo(new Span<byte>(_instanceMapped, sectionBytes.Length));
    }

    /// <summary>Records the section draw into an already begun render pass. Does nothing when there are no sections.</summary>
    public void Record(VkCommandBuffer commandBuffer, VkExtent2D extent, NodeView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count == 0)
        {
            return;
        }

        var viewport = new VkViewport { x = 0f, y = 0f, width = extent.width, height = extent.height, minDepth = 0f, maxDepth = 1f };
        var scissor = new VkRect2D { offset = new VkOffset2D(0, 0), extent = extent };
        _deviceApi.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
        _deviceApi.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
        _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);
        VkBuffer* buffers = stackalloc VkBuffer[2] { _cornerBuffer, _instanceBuffer };
        ulong* offsets = stackalloc ulong[2] { 0, 0 };
        _deviceApi.vkCmdBindVertexBuffers(commandBuffer, 0, 2, buffers, offsets);
        float* push = stackalloc float[4] { view.OffsetX, view.OffsetY, view.ScaleX, view.ScaleY };
        _deviceApi.vkCmdPushConstants(commandBuffer, _layout, VkShaderStageFlags.Vertex, 0, _pushConstantBytes, push);
        _deviceApi.vkCmdDraw(commandBuffer, 4, (uint)_count, 0, 0);
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
    }

    private void CreatePipeline()
    {
        VkShaderModule vert = CreateShaderModule("section.vert.spv");
        VkShaderModule frag = CreateShaderModule("section.frag.spv");
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

            VkVertexInputBindingDescription[] bindings = SectionPacking.Bindings;
            VkVertexInputAttributeDescription[] attributes = SectionPacking.Attributes;
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
                var viewport = new VkPipelineViewportStateCreateInfo { sType = VkStructureType.PipelineViewportStateCreateInfo, viewportCount = 1, scissorCount = 1 };
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
                var depthStencil = new VkPipelineDepthStencilStateCreateInfo
                {
                    sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
                    depthTestEnable = false,
                    depthWriteEnable = false,
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
                    pDepthStencilState = _depthAttachment ? &depthStencil : null,
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
                    throw new InvalidOperationException($"Vulkan section pipeline could not be created ({created}).");
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
        using Stream stream = typeof(SectionPass).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{resourceName}")
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
            throw new InvalidOperationException($"Vulkan could not map a section buffer ({mapped}).");
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

        throw new InvalidOperationException("Vulkan device has no host-visible, host-coherent memory for section buffers.");
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
