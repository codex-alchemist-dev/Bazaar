using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

// Density path (doc constellation-scale-and-lod, section 5, far zoom): every node is binned into a fixed
// screen-space grid by a compute pass, then the grid is shaded with one fullscreen draw. The per-frame cost
// depends on the grid size and on a compute pass over the nodes, not on per-node rasterisation.
public sealed unsafe partial class NodeBenchmark
{
    /// <summary>Density grid resolution. 512 x 288 cells is 147 456 counters, 576 KiB.</summary>
    public const int DensityCellsX = 512;
    public const int DensityCellsY = 288;
    public const int DensityCellBytes = DensityCellsX * DensityCellsY * sizeof(uint);

    private const uint _computeGroupSize = 256;
    private const uint _maxGroupsPerDimension = 65535;

    private VkBuffer _densityBuffer;
    private VkDeviceMemory _densityMemory;
    private VkShaderModule _densityComputeModule;
    private VkShaderModule _densityVertexModule;
    private VkShaderModule _densityFragmentModule;
    private VkPipelineLayout _densityComputeLayout;
    private VkPipelineLayout _densityGraphicsLayout;
    private VkPipeline _densityComputePipeline;
    private VkPipeline _densityGraphicsPipeline;

    /// <summary>Measures the density path over all <paramref name="nodeCount"/> resident nodes per frame.</summary>
    public FrameStats MeasureDensity(int nodeCount, int warmupFrames, int measuredFrames)
    {
        if (nodeCount < 1 || nodeCount > _nodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeCount));
        }

        for (int i = 0; i < warmupFrames; i++)
        {
            RenderDensityFrame(nodeCount, out _);
        }

        var wallMs = new double[measuredFrames];
        var gpuMs = new double[measuredFrames];
        for (int i = 0; i < measuredFrames; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            RenderDensityFrame(nodeCount, out double gpu);
            stopwatch.Stop();
            wallMs[i] = stopwatch.Elapsed.TotalMilliseconds;
            gpuMs[i] = gpu;
        }

        return new FrameStats(nodeCount, Percentile(wallMs, 0.5), Percentile(wallMs, 0.95), wallMs.Max(),
            Percentile(gpuMs, 0.5), Percentile(gpuMs, 0.95));
    }

    private void CreateDensityResources(string shaderDirectory)
    {
        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = (ulong)DensityCellBytes,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&bufferInfo, null, out _densityBuffer), "vkCreateBuffer(density)");
        AllocateAndBind(_densityBuffer, VkMemoryPropertyFlags.DeviceLocal, out _densityMemory);

        _densityComputeModule = CreateShaderModule(Path.Combine(shaderDirectory, "density.comp.spv"));
        _densityVertexModule = CreateShaderModule(Path.Combine(shaderDirectory, "density.vert.spv"));
        _densityFragmentModule = CreateShaderModule(Path.Combine(shaderDirectory, "density.frag.spv"));

        VkDescriptorSetLayout setLayout = _setLayout;
        var computePush = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = (uint)sizeof(DensityPush) };
        var computeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &computePush,
        };
        Check(_deviceApi.vkCreatePipelineLayout(&computeLayoutInfo, null, out _densityComputeLayout), "vkCreatePipelineLayout(density compute)");

        var graphicsPush = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Fragment, offset = 0, size = (uint)sizeof(DensityPush) };
        var graphicsLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &graphicsPush,
        };
        Check(_deviceApi.vkCreatePipelineLayout(&graphicsLayoutInfo, null, out _densityGraphicsLayout), "vkCreatePipelineLayout(density graphics)");

        byte* entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        var computeStage = new VkComputePipelineCreateInfo
        {
            sType = VkStructureType.ComputePipelineCreateInfo,
            stage = new VkPipelineShaderStageCreateInfo
            {
                sType = VkStructureType.PipelineShaderStageCreateInfo,
                stage = VkShaderStageFlags.Compute,
                module = _densityComputeModule,
                pName = entry,
            },
            layout = _densityComputeLayout,
        };
        VkPipeline computePipeline;
        Check(_deviceApi.vkCreateComputePipelines(VkPipelineCache.Null, 1, &computeStage, null, &computePipeline), "vkCreateComputePipelines(density)");
        _densityComputePipeline = computePipeline;

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Vertex, module = _densityVertexModule, pName = entry };
        stages[1] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Fragment, module = _densityFragmentModule, pName = entry };
        var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VkStructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { sType = VkStructureType.PipelineInputAssemblyStateCreateInfo, topology = VkPrimitiveTopology.TriangleList };
        var viewport = new VkViewport(0, 0, _width, _height, 0f, 1f);
        var scissor = new VkRect2D(0, 0, _width, _height);
        var viewportState = new VkPipelineViewportStateCreateInfo
        {
            sType = VkStructureType.PipelineViewportStateCreateInfo,
            viewportCount = 1,
            pViewports = &viewport,
            scissorCount = 1,
            pScissors = &scissor,
        };
        var rasterization = new VkPipelineRasterizationStateCreateInfo
        {
            sType = VkStructureType.PipelineRasterizationStateCreateInfo,
            polygonMode = VkPolygonMode.Fill,
            cullMode = VkCullModeFlags.None,
            frontFace = VkFrontFace.CounterClockwise,
            lineWidth = 1f,
        };
        var multisample = new VkPipelineMultisampleStateCreateInfo { sType = VkStructureType.PipelineMultisampleStateCreateInfo, rasterizationSamples = VkSampleCountFlags.Count1 };
        var blendAttachment = new VkPipelineColorBlendAttachmentState
        {
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
        };
        var colorBlend = new VkPipelineColorBlendStateCreateInfo { sType = VkStructureType.PipelineColorBlendStateCreateInfo, attachmentCount = 1, pAttachments = &blendAttachment };
        VkFormat colorFormat = VkFormat.R8G8B8A8Unorm;
        var renderingInfo = new VkPipelineRenderingCreateInfo
        {
            sType = VkStructureType.PipelineRenderingCreateInfo,
            colorAttachmentCount = 1,
            pColorAttachmentFormats = &colorFormat,
        };
        var graphicsInfo = new VkGraphicsPipelineCreateInfo
        {
            sType = VkStructureType.GraphicsPipelineCreateInfo,
            pNext = &renderingInfo,
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &rasterization,
            pMultisampleState = &multisample,
            pColorBlendState = &colorBlend,
            layout = _densityGraphicsLayout,
        };
        VkPipeline graphicsPipeline;
        Check(_deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &graphicsInfo, null, &graphicsPipeline), "vkCreateGraphicsPipelines(density)");
        _densityGraphicsPipeline = graphicsPipeline;
    }

    private void RenderDensityFrame(int nodeCount, out double gpuMs)
    {
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(density)");
        _deviceApi.vkResetFences(1, &fence);

        Check(_deviceApi.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(density)");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer(density)");
        _deviceApi.vkCmdResetQueryPool(commandBuffer, _queryPool, 0, 2);
        _deviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.TopOfPipe, _queryPool, 0);

        // Clear the grid, then make the clear visible to the compute pass.
        _deviceApi.vkCmdFillBuffer(commandBuffer, _densityBuffer, 0, (ulong)DensityCellBytes, 0);
        var clearBarrier = BufferBarrier(_densityBuffer, (ulong)DensityCellBytes, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, default, 0, null, 1, &clearBarrier, 0, null);

        VkDescriptorSet set = _descriptorSet;
        var push = new DensityPush
        {
            Count = (uint)nodeCount,
            CellsX = DensityCellsX,
            CellsY = DensityCellsY,
            Transform = new Vector4(PositionScale, PositionScale * _width / (float)_height, PositionOffset, 0f),
        };

        _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _densityComputePipeline);
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, _densityComputeLayout, 0, 1, &set, 0, null);
        _deviceApi.vkCmdPushConstants(commandBuffer, _densityComputeLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(DensityPush), &push);
        uint groups = (uint)((nodeCount + _computeGroupSize - 1) / _computeGroupSize);
        uint groupsX = Math.Min(groups, _maxGroupsPerDimension);
        uint groupsY = (groups + groupsX - 1) / groupsX;
        _deviceApi.vkCmdDispatch(commandBuffer, groupsX, groupsY, 1);

        // Make the counters visible to the fragment shader, then shade the grid with one fullscreen triangle.
        var shadeBarrier = BufferBarrier(_densityBuffer, (ulong)DensityCellBytes, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead);
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.FragmentShader, default, 0, null, 1, &shadeBarrier, 0, null);

        VkImageView imageView = _imageView;
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = imageView,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
        };
        var renderingInfo = new VkRenderingInfo
        {
            sType = VkStructureType.RenderingInfo,
            renderArea = new VkRect2D(0, 0, _width, _height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
        };
        _deviceApi.vkCmdBeginRendering(commandBuffer, &renderingInfo);
        _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, _densityGraphicsPipeline);
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics, _densityGraphicsLayout, 0, 1, &set, 0, null);
        _deviceApi.vkCmdPushConstants(commandBuffer, _densityGraphicsLayout, VkShaderStageFlags.Fragment, 0, (uint)sizeof(DensityPush), &push);
        _deviceApi.vkCmdDraw(commandBuffer, 3, 1, 0, 0);
        _deviceApi.vkCmdEndRendering(commandBuffer);

        _deviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.BottomOfPipe, _queryPool, 1);
        Check(_deviceApi.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer(density)");

        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit(density)");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(density frame)");

        ulong* timestamps = stackalloc ulong[2];
        Check(_deviceApi.vkGetQueryPoolResults(_queryPool, 0, 2, 2 * sizeof(ulong), timestamps, sizeof(ulong),
            VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults(density)");
        gpuMs = (timestamps[1] - timestamps[0]) * _timestampPeriodNs / 1_000_000.0;
    }

    private static VkBufferMemoryBarrier BufferBarrier(VkBuffer buffer, ulong size, VkAccessFlags source, VkAccessFlags destination) =>
        new()
        {
            sType = VkStructureType.BufferMemoryBarrier,
            srcAccessMask = source,
            dstAccessMask = destination,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            buffer = buffer,
            offset = 0,
            size = size,
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct DensityPush
    {
        public uint Count;
        public uint CellsX;
        public uint CellsY;
        public uint Pad;
        public Vector4 Transform;
    }
}
