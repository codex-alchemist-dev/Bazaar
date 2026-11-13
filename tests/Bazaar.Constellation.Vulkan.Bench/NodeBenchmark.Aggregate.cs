using System.Diagnostics;
using System.Numerics;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

// Per-tile aggregate path. The density grid is built once per graph version by streaming nodes through the
// compute pass in chunks, so the whole graph never has to be resident on the GPU at the same time. A frame
// then only shades the finished grid, so its cost does not depend on the node count (design doc section 3, 4).
public sealed unsafe partial class NodeBenchmark
{
    private VkBuffer _streamBuffer;
    private VkDeviceMemory _streamMemory;
    private VkBuffer _streamStaging;
    private VkDeviceMemory _streamStagingMemory;
    private int _streamCapacity;

    /// <summary>
    /// Builds the per-cell aggregate for <paramref name="totalNodes"/> nodes. Nodes are generated and uploaded in
    /// chunks of <paramref name="chunkNodes"/>, binned into the same grid, then replaced by the next chunk.
    /// Positions come from the global index, so the result does not depend on the chunk size.
    /// Returns the wall time of the whole build in milliseconds.
    /// </summary>
    public double BuildAggregate(long totalNodes, int chunkNodes)
    {
        EnsureStreamBuffers(chunkNodes);
        var stopwatch = Stopwatch.StartNew();
        SubmitAndWait(commandBuffer => _deviceApi.vkCmdFillBuffer(commandBuffer, _densityBuffer, 0, (ulong)DensityCellBytes, 0));

        _nodeBuffer = _streamBuffer;
        for (long first = 0; first < totalNodes; first += chunkNodes)
        {
            int count = (int)Math.Min(chunkNodes, totalNodes - first);
            FillStaging(first, count);
            CopyBuffer(_streamStaging, _streamBuffer, (ulong)count * NodeBytes);
            _nodeCount = count;
            UpdateDescriptor();
            SubmitAndWait(commandBuffer => RecordBin(commandBuffer, count));
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Times the per-frame cost once the aggregate exists: one fullscreen shade of the grid.</summary>
    public FrameStats MeasureAggregateFrame(int warmupFrames, int measuredFrames)
    {
        for (int i = 0; i < warmupFrames; i++)
        {
            RenderShadeFrame(out _);
        }

        var wallMs = new double[measuredFrames];
        var gpuMs = new double[measuredFrames];
        for (int i = 0; i < measuredFrames; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            RenderShadeFrame(out double gpu);
            stopwatch.Stop();
            wallMs[i] = stopwatch.Elapsed.TotalMilliseconds;
            gpuMs[i] = gpu;
        }

        return new FrameStats(0, Percentile(wallMs, 0.5), Percentile(wallMs, 0.95), wallMs.Max(),
            Percentile(gpuMs, 0.5), Percentile(gpuMs, 0.95));
    }

    private void EnsureStreamBuffers(int chunkNodes)
    {
        if (_streamCapacity >= chunkNodes)
        {
            return;
        }

        _streamCapacity = chunkNodes;
        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = (ulong)chunkNodes * NodeBytes,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&bufferInfo, null, out _streamBuffer), "vkCreateBuffer(stream)");
        AllocateAndBind(_streamBuffer, VkMemoryPropertyFlags.DeviceLocal, out _streamMemory);
        CreateHostBuffer((ulong)chunkNodes * NodeBytes, out _streamStaging, out _streamStagingMemory);
    }

    private void FillStaging(long first, int count)
    {
        void* mapped;
        Check(_deviceApi.vkMapMemory(_streamStagingMemory, 0, (ulong)count * NodeBytes, 0, &mapped), "vkMapMemory(stream staging)");
        var span = new Span<float>(mapped, count * 4);
        for (int i = 0; i < count; i++)
        {
            ulong global = (ulong)(first + i);
            span[i * 4] = Hash01(global, 0x51ED2701UL) * 2f - 1f;
            span[i * 4 + 1] = Hash01(global, 0x2F1B3C5DUL) * 2f - 1f;
            span[i * 4 + 2] = (float)(first + i) % 4096f;
            span[i * 4 + 3] = 0f;
        }

        _deviceApi.vkUnmapMemory(_streamStagingMemory);
    }

    /// <summary>Deterministic value in [0, 1) from a global index, so chunking never changes positions.</summary>
    private static float Hash01(ulong index, ulong salt)
    {
        ulong h = (index + salt) * 0x9E3779B97F4A7C15UL;
        h ^= h >> 31;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 29;
        return (h >> 40) * (1f / (1 << 24));
    }

    private void SubmitAndWait(Action<VkCommandBuffer> record)
    {
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        Check(_deviceApi.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(aggregate)");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer(aggregate)");
        record(commandBuffer);
        Check(_deviceApi.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer(aggregate)");
        _deviceApi.vkResetFences(1, &fence);
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit(aggregate)");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(aggregate)");
    }

    private DensityPush MakePush(int count) => new()
    {
        Count = (uint)count,
        CellsX = DensityCellsX,
        CellsY = DensityCellsY,
        Transform = new Vector4(PositionScale, PositionScale * _width / (float)_height, PositionOffset, 0f),
    };

    private void RecordBin(VkCommandBuffer commandBuffer, int count)
    {
        VkDescriptorSet set = _descriptorSet;
        DensityPush push = MakePush(count);
        VkBufferMemoryBarrier barrier = BufferBarrier(_streamBuffer, (ulong)count * NodeBytes, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead);
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, default, 0, null, 1, &barrier, 0, null);
        _deviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _densityComputePipeline);
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, _densityComputeLayout, 0, 1, &set, 0, null);
        _deviceApi.vkCmdPushConstants(commandBuffer, _densityComputeLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(DensityPush), &push);
        uint groups = (uint)((count + _computeGroupSize - 1) / _computeGroupSize);
        uint groupsX = Math.Min(groups, _maxGroupsPerDimension);
        _deviceApi.vkCmdDispatch(commandBuffer, groupsX, (groups + groupsX - 1) / groupsX, 1);
    }

    private void RenderShadeFrame(out double gpuMs)
    {
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(shade)");
        _deviceApi.vkResetFences(1, &fence);
        Check(_deviceApi.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(shade)");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer(shade)");
        _deviceApi.vkCmdResetQueryPool(commandBuffer, _queryPool, 0, 2);
        _deviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.TopOfPipe, _queryPool, 0);

        VkDescriptorSet set = _descriptorSet;
        DensityPush push = MakePush(0);
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = _imageView,
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
        Check(_deviceApi.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer(shade)");
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit(shade)");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(shade frame)");

        ulong* timestamps = stackalloc ulong[2];
        Check(_deviceApi.vkGetQueryPoolResults(_queryPool, 0, 2, 2 * sizeof(ulong), timestamps, sizeof(ulong),
            VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults(shade)");
        gpuMs = (timestamps[1] - timestamps[0]) * _timestampPeriodNs / 1_000_000.0;
    }
}
