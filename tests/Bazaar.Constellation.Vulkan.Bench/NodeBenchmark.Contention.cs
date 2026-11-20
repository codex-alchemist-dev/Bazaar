using System.Diagnostics;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

// Section 10.12: the combined frame (cull, decimation, points, edges) submitted in different ways, to see what the driver and the OS
// scheduler let a frame do while another process keeps the GPU busy.
public sealed unsafe partial class NodeBenchmark
{
    /// <summary>
    /// The points variant of the combined frame, recorded as <paramref name="parts"/> command buffers (1, 2 or 4) submitted back to back
    /// in <paramref name="framesInFlight"/> slots. Wall time is the interval between successive frame submissions; with one frame in
    /// flight that is the whole frame including the wait, with more it is the cadence (the latency of one frame is longer).
    /// GPU statistics are not collected here (they are the same as <see cref="MeasureCombined"/>); the GPU columns are zero.
    /// Needs <see cref="BuildTiles"/> for the same topology, dimension, N and point count.
    /// </summary>
    public CellStats MeasureCombinedSubmitted(Topology topology, int dim, long n, Viewpoint view, int points, int edges, int parts, int framesInFlight,
        int warmup, int measured, float lodMinPixels, float lodMaxPixels)
    {
        int total = warmup + measured;
        var buffers = new VkCommandBuffer[framesInFlight * parts];
        var fences = new VkFence[framesInFlight];
        var inFlight = new bool[framesInFlight];
        var allocate = new VkCommandBufferAllocateInfo { sType = VkStructureType.CommandBufferAllocateInfo, commandPool = _commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = (uint)buffers.Length };
        fixed (VkCommandBuffer* pointer = buffers)
        {
            Check(_deviceApi.vkAllocateCommandBuffers(&allocate, pointer), "vkAllocateCommandBuffers(contention)");
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
            Check(_deviceApi.vkCreateFence(&fenceInfo, null, out fences[slot]), "vkCreateFence(contention)");
        }

        TopoPush push = MakeTopoPush(topology, dim, n, view);
        push.Stride = (uint)Math.Max(1, n / points);
        push.B = (uint)points;
        push.A = 0;
        var submitted = new long[total];
        for (int frame = 0; frame < total; frame++)
        {
            int slot = frame % framesInFlight;
            if (inFlight[slot])
            {
                VkFence waiting = fences[slot];
                Check(_deviceApi.vkWaitForFences(1, &waiting, true, _fenceTimeoutNs * 4), "vkWaitForFences(contention)");
                _deviceApi.vkResetFences(1, &waiting);
            }

            for (int part = 0; part < parts; part++)
            {
                VkCommandBuffer cb = buffers[(slot * parts) + part];
                Check(_deviceApi.vkResetCommandBuffer(cb, 0), "vkResetCommandBuffer(contention)");
                var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
                Check(_deviceApi.vkBeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer(contention)");
                RecordCombinedPart(cb, push, part, parts, n, edges, lodMinPixels, lodMaxPixels);
                Check(_deviceApi.vkEndCommandBuffer(cb), "vkEndCommandBuffer(contention)");
                VkCommandBuffer submitBuffer = cb;
                var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &submitBuffer };
                Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, part == parts - 1 ? fences[slot] : VkFence.Null), "vkQueueSubmit(contention)");
            }

            inFlight[slot] = true;
            if (framesInFlight == 1)
            {
                VkFence only = fences[slot];
                Check(_deviceApi.vkWaitForFences(1, &only, true, _fenceTimeoutNs * 4), "vkWaitForFences(contention frame)");
                _deviceApi.vkResetFences(1, &only);
                inFlight[slot] = false;
                submitted[frame] = Stopwatch.GetTimestamp();
            }
            else
            {
                submitted[frame] = Stopwatch.GetTimestamp();
            }
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            if (inFlight[slot])
            {
                VkFence waiting = fences[slot];
                Check(_deviceApi.vkWaitForFences(1, &waiting, true, _fenceTimeoutNs * 4), "vkWaitForFences(contention drain)");
            }
        }

        _deviceApi.vkDeviceWaitIdle();
        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _deviceApi.vkDestroyFence(fences[slot], null);
        }

        fixed (VkCommandBuffer* pointer = buffers)
        {
            _deviceApi.vkFreeCommandBuffers(_commandPool, (uint)buffers.Length, pointer);
        }

        var wall = new double[measured];
        for (int i = 0; i < measured; i++)
        {
            int frame = warmup + i;
            wall[i] = (submitted[frame] - submitted[frame - 1]) * 1000.0 / Stopwatch.Frequency;
        }

        return new CellStats(measured, 0, 0, 0, Percentile(wall, 0.5), Percentile(wall, 0.95), Percentile(wall, 0.99), wall.Max(), new Dictionary<string, double>());
    }

    private void RecordCombinedPart(VkCommandBuffer cb, TopoPush push, int part, int parts, long n, int edges, float lodMin, float lodMax)
    {
        int last = parts - 1;
        // Steps: 0 reset, 1 cull, 2 claim, 3 select. Parts: 1 = all, 2 = steps then draw, 4 = [0,1] [2] [3] [draw].
        bool Has(int step) => (parts <= 2 ? 0 : (step <= 1 ? 0 : step - 1)) == part;

        // A global barrier at the start of every command buffer orders it after everything submitted before it (the previous part or frame).
        GlobalBarrier(cb, VkPipelineStageFlags.AllCommands, VkPipelineStageFlags.AllCommands, VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite, VkAccessFlags.MemoryRead | VkAccessFlags.MemoryWrite);
        if (Has(0))
        {
            uint* cmdHeader = stackalloc uint[4];
            cmdHeader[0] = 0;
            cmdHeader[1] = 0;
            cmdHeader[2] = 0;
            cmdHeader[3] = 0;
            uint* survivorHeader = stackalloc uint[8];
            survivorHeader[0] = 1;
            for (int i = 1; i < 8; i++)
            {
                survivorHeader[i] = 0;
            }

            _deviceApi.vkCmdUpdateBuffer(cb, _cmdBuffer, 0, 16, cmdHeader);
            _deviceApi.vkCmdUpdateBuffer(cb, _cullSurvivors, 0, 32, survivorHeader);
            _deviceApi.vkCmdFillBuffer(cb, _cullCells, 0, _cullCellBytes, 0);
            GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        }

        if (Has(1))
        {
            BindCullSet(cb, VkPipelineBindPoint.Compute);
            PushCull(cb, _cullLayout, push);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileCullPipeline);
            Dispatch(cb, TileCount);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        }

        if (Has(2))
        {
            BindCullSet(cb, VkPipelineBindPoint.Compute);
            PushCull(cb, _cullLayout, push);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _claimTilesPipeline);
            _deviceApi.vkCmdDispatch(cb, TileCount, 1, 1);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        }

        if (Has(3))
        {
            BindCullSet(cb, VkPipelineBindPoint.Compute);
            PushCull(cb, _cullLayout, push);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _selectTilesPipeline);
            _deviceApi.vkCmdDispatch(cb, TileCount, 1, 1);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader,
                VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead);
        }

        if (part == last)
        {
            BeginCullRendering(cb, false, false);
            BindCullSet(cb, VkPipelineBindPoint.Graphics);
            PushCull(cb, _cullLayout, push);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pointsDecPipeline);
            _deviceApi.vkCmdDrawIndirect(cb, _cullSurvivors, 0, 1, 16);
            BindTopoSet(cb, VkPipelineBindPoint.Graphics);
            DrawEdges(cb, push, n, edges, lodMin, lodMax);
            _deviceApi.vkCmdEndRendering(cb);
        }
    }
}
