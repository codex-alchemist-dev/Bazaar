using System.Diagnostics;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

// Overdraw mitigations and frame pipelining (doc constellation-scale-and-lod, section 10.9).
public sealed unsafe partial class NodeBenchmark
{
    private void CreateHostReadback()
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = 256,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&info, null, out _hostReadback), "vkCreateBuffer(readback)");
        AllocateAndBind(_hostReadback, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out _hostReadbackMemory);
        void* mapped;
        Check(_deviceApi.vkMapMemory(_hostReadbackMemory, 0, 256, 0, &mapped), "vkMapMemory(readback)");
        _hostReadbackMapped = (uint*)mapped;
    }

    private void GlobalBarrier(VkCommandBuffer cb, VkPipelineStageFlags source, VkPipelineStageFlags destination, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
    {
        var barrier = new VkMemoryBarrier { sType = VkStructureType.MemoryBarrier, srcAccessMask = sourceAccess, dstAccessMask = destinationAccess };
        _deviceApi.vkCmdPipelineBarrier(cb, source, destination, default, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>
    /// Point decimation: a compute pass lets one sampled point claim each 2x2-pixel cell, a second pass appends the winners to
    /// a survivor list, and only the survivors are drawn with an indirect draw. Points outside the view are dropped too.
    /// </summary>
    public CellStats MeasureDecimated(Topology topology, int dim, long n, Viewpoint view, int points, int edges, int warmup, int measured,
        float lodMinPixels = 0f, float lodMaxPixels = 0f, float pointSizeOverride = 0f) =>
        Measure(warmup, measured, ["clear", "claim", "select", "points", "edges"], () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            if (pointSizeOverride > 0f)
            {
                push.PointSize = pointSizeOverride;
            }

            push.Stride = (uint)Math.Max(1, n / points);
            push.A = CellsX;
            push.B = (uint)points;
            return RunSegment((cb, stamp) =>
            {
                uint* header = stackalloc uint[4];
                header[0] = 1;
                header[1] = 0;
                header[2] = 0;
                header[3] = 0;
                _deviceApi.vkCmdFillBuffer(cb, _screenBuffer, 0, (ulong)CellsX * CellsY * 4, 0);
                _deviceApi.vkCmdUpdateBuffer(cb, _survivorBuffer, 0, 16, header);
                GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                BindTopoSet(cb, VkPipelineBindPoint.Compute);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _claimPipeline);
                Push(cb, push);
                Dispatch(cb, points);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _selectPipeline);
                Push(cb, push);
                Dispatch(cb, points);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader,
                    VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead);
                stamp();
                BeginTopoRendering(cb);
                BindTopoSet(cb, VkPipelineBindPoint.Graphics);
                Push(cb, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pointsIndirectPipeline);
                _deviceApi.vkCmdDrawIndirect(cb, _survivorBuffer, 0, 1, 16);
                stamp();
                DrawEdges(cb, push, n, edges, lodMinPixels, lodMaxPixels);
                stamp();
                _deviceApi.vkCmdEndRendering(cb);
            });
        });

    /// <summary>Binds and draws the edge pass (plain or screen-length LOD) inside an open rendering scope.</summary>
    private void DrawEdges(VkCommandBuffer cb, TopoPush push, long n, int edges, float lodMinPixels, float lodMaxPixels)
    {
        push.Stride = (uint)Math.Max(1, edges > 0 ? n / edges : 1);
        bool lod = lodMaxPixels > 0f;
        if (lod)
        {
            push.A = BitConverter.SingleToUInt32Bits(lodMinPixels);
            push.B = BitConverter.SingleToUInt32Bits(lodMaxPixels);
        }

        Push(cb, push);
        _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, lod ? _edgesLodPipeline : _edgesPipeline);
        _deviceApi.vkCmdDraw(cb, (uint)edges * 2, 1, 0, 0);
    }

    /// <summary>
    /// Counts the pixels the aggregate path covers for this view (non-empty cells of the 1280x720 grid). Points per pixel for a
    /// draw of M points is estimated as M divided by this. Needs <see cref="BuildAggregate"/> first.
    /// Returns the covered pixel count and the GPU milliseconds of the counting pass.
    /// </summary>
    public (long Occupied, double CountMs) EstimateOccupiedPixels(Topology topology, int dim, long n, Viewpoint view)
    {
        MeasureAggregate(topology, dim, n, view, 0, 0, 1);
        TopoPush push = MakeTopoPush(topology, dim, n, view);
        push.B = ScreenCells;
        double[] times = RunSegment((cb, stamp) =>
        {
            uint* zero = stackalloc uint[4];
            zero[0] = 0;
            zero[1] = 0;
            zero[2] = 0;
            zero[3] = 0;
            _deviceApi.vkCmdUpdateBuffer(cb, _survivorBuffer, 16, 16, zero);
            GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            BindTopoSet(cb, VkPipelineBindPoint.Compute);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _occupancyPipeline);
            Push(cb, push);
            Dispatch(cb, ScreenCells);
            stamp();
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkAccessFlags.ShaderWrite, VkAccessFlags.TransferRead);
            var region = new VkBufferCopy { srcOffset = 16, dstOffset = 0, size = 4 };
            _deviceApi.vkCmdCopyBuffer(cb, _survivorBuffer, _hostReadback, 1, &region);
            GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, VkAccessFlags.TransferWrite, VkAccessFlags.HostRead);
        });
        return (_hostReadbackMapped[0], times[0]);
    }

    // ---- frames in flight ----

    /// <summary>
    /// The draw frame (points plus edges) with up to framesInFlight frames queued: the host waits only for the frame that used
    /// the same slot, not for the one just submitted. Wall time is the interval between successive submit returns; GPU time is
    /// the sum of per-pass timestamps of each frame, read when its slot is reused.
    /// </summary>
    public CellStats MeasureDrawPipelined(Topology topology, int dim, long n, Viewpoint view, int instances, int edges, int framesInFlight, int warmup, int measured,
        float lodMinPixels = 0f, float lodMaxPixels = 0f)
    {
        int total = warmup + measured;
        var commandBuffers = new VkCommandBuffer[framesInFlight];
        var fences = new VkFence[framesInFlight];
        var pools = new VkQueryPool[framesInFlight];
        var inFlight = new int[framesInFlight];
        Array.Fill(inFlight, -1);
        var allocate = new VkCommandBufferAllocateInfo { sType = VkStructureType.CommandBufferAllocateInfo, commandPool = _commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = (uint)framesInFlight };
        fixed (VkCommandBuffer* buffers = commandBuffers)
        {
            Check(_deviceApi.vkAllocateCommandBuffers(&allocate, buffers), "vkAllocateCommandBuffers(pipelined)");
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
            Check(_deviceApi.vkCreateFence(&fenceInfo, null, out fences[slot]), "vkCreateFence(pipelined)");
            var queryInfo = new VkQueryPoolCreateInfo { sType = VkStructureType.QueryPoolCreateInfo, queryType = VkQueryType.Timestamp, queryCount = 4 };
            Check(_deviceApi.vkCreateQueryPool(&queryInfo, null, out pools[slot]), "vkCreateQueryPool(pipelined)");
        }

        var gpu = new double[total];
        var submitted = new long[total];
        double ReadFrame(int slot)
        {
            VkFence fence = fences[slot];
            Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(pipelined)");
            _deviceApi.vkResetFences(1, &fence);
            ulong* ticks = stackalloc ulong[3];
            Check(_deviceApi.vkGetQueryPoolResults(pools[slot], 0, 3, 3 * sizeof(ulong), ticks, sizeof(ulong), VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults(pipelined)");
            return (ticks[2] - ticks[0]) * _timestampPeriodNs / 1_000_000.0;
        }

        TopoPush push = MakeTopoPush(topology, dim, n, view);
        for (int frame = 0; frame < total; frame++)
        {
            int slot = frame % framesInFlight;
            if (inFlight[slot] >= 0)
            {
                gpu[inFlight[slot]] = ReadFrame(slot);
            }

            VkCommandBuffer cb = commandBuffers[slot];
            VkQueryPool pool = pools[slot];
            Check(_deviceApi.vkResetCommandBuffer(cb, 0), "vkResetCommandBuffer(pipelined)");
            var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            Check(_deviceApi.vkBeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer(pipelined)");
            _deviceApi.vkCmdResetQueryPool(cb, pool, 0, 4);
            _deviceApi.vkCmdWriteTimestamp(cb, VkPipelineStageFlags.TopOfPipe, pool, 0);
            BeginTopoRendering(cb);
            BindTopoSet(cb, VkPipelineBindPoint.Graphics);
            TopoPush pointsPush = push;
            pointsPush.Stride = (uint)Math.Max(1, n / instances);
            Push(cb, pointsPush);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pointsPipeline);
            _deviceApi.vkCmdDraw(cb, 1, (uint)instances, 0, 0);
            _deviceApi.vkCmdWriteTimestamp(cb, VkPipelineStageFlags.BottomOfPipe, pool, 1);
            DrawEdges(cb, push, n, edges, lodMinPixels, lodMaxPixels);
            _deviceApi.vkCmdWriteTimestamp(cb, VkPipelineStageFlags.BottomOfPipe, pool, 2);
            _deviceApi.vkCmdEndRendering(cb);
            Check(_deviceApi.vkEndCommandBuffer(cb), "vkEndCommandBuffer(pipelined)");
            var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &cb };
            Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, fences[slot]), "vkQueueSubmit(pipelined)");
            inFlight[slot] = frame;
            submitted[frame] = Stopwatch.GetTimestamp();
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            if (inFlight[slot] >= 0)
            {
                gpu[inFlight[slot]] = ReadFrame(slot);
            }
        }

        _deviceApi.vkDeviceWaitIdle();
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _deviceApi.vkDestroyFence(fences[slot], null);
            _deviceApi.vkDestroyQueryPool(pools[slot], null);
        }

        fixed (VkCommandBuffer* buffers = commandBuffers)
        {
            _deviceApi.vkFreeCommandBuffers(_commandPool, (uint)framesInFlight, buffers);
        }

        var wall = new double[measured];
        var gpuMeasured = new double[measured];
        for (int i = 0; i < measured; i++)
        {
            int frame = warmup + i;
            wall[i] = (submitted[frame] - submitted[frame - 1]) * 1000.0 / Stopwatch.Frequency;
            gpuMeasured[i] = gpu[frame];
        }

        return new CellStats(measured, Percentile(gpuMeasured, 0.5), Percentile(gpuMeasured, 0.95), Percentile(gpuMeasured, 0.99),
            Percentile(wall, 0.5), Percentile(wall, 0.95), Percentile(wall, 0.99), wall.Max(), new Dictionary<string, double>());
    }
}
