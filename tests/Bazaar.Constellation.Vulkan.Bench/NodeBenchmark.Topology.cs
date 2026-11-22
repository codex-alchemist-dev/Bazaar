using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>Graph shapes the procedural generator can produce from a node index alone (Shaders/topology.glsl).</summary>
public enum Topology
{
    Uniform = 0,
    Clustered = 1,
    HubAndSpoke = 2,
    Grid = 3,
    Ring = 4,
}

/// <summary>Where the camera is. The same sampled node set is drawn from each, so culling work and overdraw change.</summary>
public enum Viewpoint
{
    Whole,
    Mid,
    Close,
}

/// <summary>Frame-time statistics for one benchmark cell, with a per-pass GPU breakdown.</summary>
public sealed record CellStats(
    int Frames,
    double GpuP50, double GpuP95, double GpuP99,
    double WallP50, double WallP95, double WallP99, double WallMax,
    IReadOnlyDictionary<string, double> PassP50Ms);

// Procedural billion-node benchmark (doc constellation-scale-and-lod, sections 10 and 11).
// Nodes and edges are generated in the shaders from the node index, so nothing is stored and the graph size is just a number.
// Draw modes sample the graph with a fixed stride (the tile working set stand-in); density modes splat every node, or a
// precomputed voxel aggregate, into a screen grid.
public sealed unsafe partial class NodeBenchmark
{
    public const int ScreenWidth = 1280;
    public const int ScreenHeight = 720;
    public const int ScreenCells = ScreenWidth * ScreenHeight;

    /// <summary>Voxels per axis for the aggregate grid: 2048 x 2048 in 2D, 160 x 160 x 160 in 3D (16 MiB each).</summary>
    public static int AggregateGrid(int dim) => dim == 2 ? 2048 : 160;

    public static long AggregateCells(int dim) => dim == 2 ? 2048L * 2048 : 160L * 160 * 160;

    private const int _timestampSlots = 16;
    private const long _splatChunkNodes = 128_000_000;

    private VkQueryPool _topoQueryPool;
    private VkDescriptorSetLayout _topoSetLayout;
    private VkDescriptorPool _topoPool;
    private VkDescriptorSet _topoSet;
    private VkPipelineLayout _topoLayout;
    private VkPipeline _pointsPipeline;
    private VkPipeline _edgesPipeline;
    private VkPipeline _edgesLodPipeline;
    private VkPipeline _edgeBinPipeline;
    private VkPipeline _shadePipeline;
    private VkPipeline _splatPipeline;
    private VkPipeline _binPipeline;
    private VkPipeline _reprojectPipeline;
    private VkBuffer _voxelBuffer;
    private VkDeviceMemory _voxelMemory;
    private VkBuffer _screenBuffer;
    private VkDeviceMemory _screenMemory;

    /// <summary>Bytes of GPU memory the topology benchmark allocated (voxel grid plus screen grid).</summary>
    public static long TopologyGpuBytes => (AggregateCells(2) * 4) + (ScreenCells * 4L);

    [StructLayout(LayoutKind.Sequential)]
    private struct TopoPush
    {
        public Matrix4x4 ViewProj;
        public uint Topo;
        public uint Dim;
        public uint N;
        public uint Stride;
        public float PointSize;
        public uint A;
        public uint B;
        public uint C;
    }

    /// <summary>Creates the pipelines, descriptor set and grids the topology modes use. Call once after <see cref="Initialize"/>.</summary>
    public void InitializeTopology(string shaderDirectory)
    {
        var queryInfo = new VkQueryPoolCreateInfo
        {
            sType = VkStructureType.QueryPoolCreateInfo,
            queryType = VkQueryType.Timestamp,
            queryCount = _timestampSlots,
        };
        Check(_deviceApi.vkCreateQueryPool(&queryInfo, null, out _topoQueryPool), "vkCreateQueryPool(topology)");

        // Voxel grid is sized for the larger of the 2D and 3D aggregates (both are 16 MiB).
        _voxelBuffer = CreateStorageBuffer((ulong)(AggregateCells(2) * 4), out _voxelMemory);
        _screenBuffer = CreateStorageBuffer((ulong)(ScreenCells * 4L), out _screenMemory);

        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        for (uint b = 0; b < 2; b++)
        {
            bindings[b] = new VkDescriptorSetLayoutBinding
            {
                binding = b,
                descriptorType = VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute | VkShaderStageFlags.Fragment,
            };
        }

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = 2, pBindings = bindings };
        Check(_deviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out _topoSetLayout), "vkCreateDescriptorSetLayout(topology)");
        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 2 };
        var poolInfo = new VkDescriptorPoolCreateInfo { sType = VkStructureType.DescriptorPoolCreateInfo, maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };
        Check(_deviceApi.vkCreateDescriptorPool(&poolInfo, null, out _topoPool), "vkCreateDescriptorPool(topology)");
        VkDescriptorSetLayout setLayout = _topoSetLayout;
        var allocate = new VkDescriptorSetAllocateInfo { sType = VkStructureType.DescriptorSetAllocateInfo, descriptorPool = _topoPool, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(_deviceApi.vkAllocateDescriptorSets(&allocate, &set), "vkAllocateDescriptorSets(topology)");
        _topoSet = set;

        var infos = stackalloc VkDescriptorBufferInfo[2];
        infos[0] = new VkDescriptorBufferInfo { buffer = _voxelBuffer, offset = 0, range = (ulong)(AggregateCells(2) * 4) };
        infos[1] = new VkDescriptorBufferInfo { buffer = _screenBuffer, offset = 0, range = (ulong)(ScreenCells * 4L) };
        var writes = stackalloc VkWriteDescriptorSet[2];
        for (uint b = 0; b < 2; b++)
        {
            writes[b] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = _topoSet,
                dstBinding = b,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &infos[b],
            };
        }

        _deviceApi.vkUpdateDescriptorSets(2, writes, 0, null);

        var pushRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment | VkShaderStageFlags.Compute,
            offset = 0,
            size = (uint)sizeof(TopoPush),
        };
        var layoutCreate = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange,
        };
        Check(_deviceApi.vkCreatePipelineLayout(&layoutCreate, null, out _topoLayout), "vkCreatePipelineLayout(topology)");

        VkShaderModule Module(string name) => CreateShaderModule(Path.Combine(shaderDirectory, name + ".spv"));
        _pointsPipeline = CreateTopoGraphics(Module("points.vert"), Module("flat.frag"), VkPrimitiveTopology.PointList, false);
        _edgesPipeline = CreateTopoGraphics(Module("edges.vert"), Module("flat.frag"), VkPrimitiveTopology.LineList, true);
        _shadePipeline = CreateTopoGraphics(Module("shade.vert"), Module("shade.frag"), VkPrimitiveTopology.TriangleList, false);
        _edgesLodPipeline = CreateTopoGraphics(Module("edges_lod.vert"), Module("flat.frag"), VkPrimitiveTopology.LineList, true);
        _edgeBinPipeline = CreateTopoCompute(Module("edgebin.comp"));
        _splatPipeline = CreateTopoCompute(Module("splat.comp"));
        _binPipeline = CreateTopoCompute(Module("bin.comp"));
        _reprojectPipeline = CreateTopoCompute(Module("reproject.comp"));
    }

    private VkBuffer CreateStorageBuffer(ulong size, out VkDeviceMemory memory)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&info, null, out VkBuffer buffer), "vkCreateBuffer(topology)");
        AllocateAndBind(buffer, VkMemoryPropertyFlags.DeviceLocal, out memory);
        return buffer;
    }

    private VkPipeline CreateTopoCompute(VkShaderModule module)
    {
        var info = new VkComputePipelineCreateInfo
        {
            sType = VkStructureType.ComputePipelineCreateInfo,
            stage = new VkPipelineShaderStageCreateInfo
            {
                sType = VkStructureType.PipelineShaderStageCreateInfo,
                stage = VkShaderStageFlags.Compute,
                module = module,
                pName = (byte*)Marshal.StringToHGlobalAnsi("main"),
            },
            layout = _topoLayout,
        };
        VkPipeline pipeline;
        Check(_deviceApi.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateComputePipelines(topology)");
        return pipeline;
    }

    private VkPipeline CreateTopoGraphics(VkShaderModule vertex, VkShaderModule fragment, VkPrimitiveTopology topology, bool blend)
    {
        byte* entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Vertex, module = vertex, pName = entry };
        stages[1] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Fragment, module = fragment, pName = entry };
        var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VkStructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { sType = VkStructureType.PipelineInputAssemblyStateCreateInfo, topology = topology };
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
        var attachment = new VkPipelineColorBlendAttachmentState
        {
            blendEnable = blend,
            srcColorBlendFactor = VkBlendFactor.SrcAlpha,
            dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            colorBlendOp = VkBlendOp.Add,
            srcAlphaBlendFactor = VkBlendFactor.One,
            dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            alphaBlendOp = VkBlendOp.Add,
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
        };
        var colorBlend = new VkPipelineColorBlendStateCreateInfo { sType = VkStructureType.PipelineColorBlendStateCreateInfo, attachmentCount = 1, pAttachments = &attachment };
        VkFormat colorFormat = VkFormat.R8G8B8A8Unorm;
        var rendering = new VkPipelineRenderingCreateInfo { sType = VkStructureType.PipelineRenderingCreateInfo, colorAttachmentCount = 1, pColorAttachmentFormats = &colorFormat };
        var info = new VkGraphicsPipelineCreateInfo
        {
            sType = VkStructureType.GraphicsPipelineCreateInfo,
            pNext = &rendering,
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInput,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &rasterization,
            pMultisampleState = &multisample,
            pColorBlendState = &colorBlend,
            layout = _topoLayout,
        };
        VkPipeline pipeline;
        Check(_deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines(topology)");
        return pipeline;
    }

    // ---- cameras ----

    /// <summary>View-projection matrix (System.Numerics row convention; the shaders read the same bytes as column-major, which is its transpose).</summary>
    public static Matrix4x4 Camera(int dim, Viewpoint view)
    {
        float aspect = ScreenWidth / (float)ScreenHeight;
        if (dim == 2)
        {
            (float zoom, float cx, float cy) = view switch
            {
                Viewpoint.Whole => (1f, 0f, 0f),
                Viewpoint.Mid => (4f, 0.31f, 0.17f),
                _ => (32f, 0.31f, 0.17f),
            };
            float sx = zoom / aspect;
            float sy = zoom;
            return new Matrix4x4(
                sx, 0, 0, 0,
                0, sy, 0, 0,
                0, 0, 0, 0,
                -cx * sx, -cy * sy, 0.5f, 1);
        }

        (float distance, Vector3 target, float near) = view switch
        {
            Viewpoint.Whole => (3f, Vector3.Zero, 0.1f),
            Viewpoint.Mid => (0.6f, new Vector3(0.3f, 0.2f, 0.1f), 0.02f),
            _ => (0.06f, new Vector3(0.3f, 0.2f, 0.1f), 0.002f),
        };
        Vector3 direction = Vector3.Normalize(new Vector3(0.5f, 0.35f, 0.8f));
        Matrix4x4 lookAt = Matrix4x4.CreateLookAt(target + (direction * distance), target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180f, aspect, near, 50f);
        // Reversed-Z as the renderer thread uses (depth 1 at near, 0 at far): z' = w - z. No depth test in this bench.
        var reverseZ = new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 1, 1);
        return lookAt * projection * reverseZ;
    }

    private static float PointSizeFor(Viewpoint view) => view switch { Viewpoint.Whole => 1f, Viewpoint.Mid => 2f, _ => 4f };

    private static TopoPush MakeTopoPush(Topology topology, int dim, long n, Viewpoint view) => new()
    {
        ViewProj = Camera(dim, view),
        Topo = (uint)topology,
        Dim = (uint)dim,
        N = (uint)n,
        Stride = 1,
        PointSize = PointSizeFor(view),
    };

    // ---- segments and timestamps ----

    /// <summary>
    /// Records one command buffer, submits it, waits, and returns the GPU milliseconds between consecutive timestamps.
    /// <paramref name="record"/> gets the command buffer and a callback that writes the next timestamp.
    /// </summary>
    private double[] RunSegment(Action<VkCommandBuffer, Action> record)
    {
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(segment)");
        _deviceApi.vkResetFences(1, &fence);
        Check(_deviceApi.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(segment)");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkBeginCommandBuffer(commandBuffer, &begin), "vkBeginCommandBuffer(segment)");
        _deviceApi.vkCmdResetQueryPool(commandBuffer, _topoQueryPool, 0, _timestampSlots);
        _deviceApi.vkCmdWriteTimestamp(commandBuffer, VkPipelineStageFlags.TopOfPipe, _topoQueryPool, 0);
        uint stamps = 1;
        VkCommandBuffer recorded = commandBuffer;
        record(recorded, () => _deviceApi.vkCmdWriteTimestamp(recorded, VkPipelineStageFlags.BottomOfPipe, _topoQueryPool, stamps++));
        Check(_deviceApi.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer(segment)");
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit(segment)");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs * 4), "vkWaitForFences(segment frame)");
        ulong* ticks = stackalloc ulong[(int)_timestampSlots];
        Check(_deviceApi.vkGetQueryPoolResults(_topoQueryPool, 0, stamps, (nuint)(stamps * sizeof(ulong)), ticks, sizeof(ulong),
            VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults(segment)");
        var result = new double[stamps - 1];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (ticks[i + 1] - ticks[i]) * _timestampPeriodNs / 1_000_000.0;
        }

        return result;
    }

    private void BeginTopoRendering(VkCommandBuffer commandBuffer)
    {
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
    }

    private void Push(VkCommandBuffer commandBuffer, TopoPush push) =>
        _deviceApi.vkCmdPushConstants(commandBuffer, _topoLayout,
            VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment | VkShaderStageFlags.Compute, 0, (uint)sizeof(TopoPush), &push);

    private void BindTopoSet(VkCommandBuffer commandBuffer, VkPipelineBindPoint point)
    {
        VkDescriptorSet set = _topoSet;
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, point, _topoLayout, 0, 1, &set, 0, null);
    }

    private void Dispatch(VkCommandBuffer commandBuffer, long threads)
    {
        long groups = (threads + 255) / 256;
        uint x = (uint)Math.Min(groups, 65535);
        uint y = (uint)((groups + x - 1) / x);
        _deviceApi.vkCmdDispatch(commandBuffer, x, y, 1);
    }

    private void ComputeBarrier(VkCommandBuffer commandBuffer, VkBuffer buffer, ulong size, VkPipelineStageFlags source, VkPipelineStageFlags destination, VkAccessFlags srcAccess, VkAccessFlags dstAccess)
    {
        VkBufferMemoryBarrier barrier = BufferBarrier(buffer, size, srcAccess, dstAccess);
        _deviceApi.vkCmdPipelineBarrier(commandBuffer, source, destination, default, 0, null, 1, &barrier, 0, null);
    }

    // ---- frames ----

    /// <summary>Points at <paramref name="instances"/> sampled nodes plus <paramref name="edges"/> line segments, both generated from the node index.</summary>
    public CellStats MeasureDraw(Topology topology, int dim, long n, Viewpoint view, int instances, int edges, int warmup, int measured, float lodMinPixels = 0f, float lodMaxPixels = 0f) =>
        Measure(warmup, measured, ["points", "edges"], () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            return RunSegment((cb, stamp) =>
            {
                BeginTopoRendering(cb);
                BindTopoSet(cb, VkPipelineBindPoint.Graphics);
                push.Stride = (uint)Math.Max(1, n / instances);
                Push(cb, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pointsPipeline);
                _deviceApi.vkCmdDraw(cb, 1, (uint)instances, 0, 0);
                stamp();
                push.Stride = (uint)Math.Max(1, n / edges);
                bool lod = lodMaxPixels > 0f;
                if (lod)
                {
                    push.A = BitConverter.SingleToUInt32Bits(lodMinPixels);
                    push.B = BitConverter.SingleToUInt32Bits(lodMaxPixels);
                }

                Push(cb, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, lod ? _edgesLodPipeline : _edgesPipeline);
                _deviceApi.vkCmdDraw(cb, (uint)edges * 2, 1, 0, 0);
                stamp();
                _deviceApi.vkCmdEndRendering(cb);
            });
        });

    /// <summary>Builds the voxel aggregate once: every node binned from its index. Returns the wall milliseconds.</summary>
    public double BuildAggregate(Topology topology, int dim, long n, int bakedEdges = 0)
    {
        var stopwatch = Stopwatch.StartNew();
        RunSegment((cb, stamp) =>
        {
            _deviceApi.vkCmdFillBuffer(cb, _voxelBuffer, 0, (ulong)(AggregateCells(2) * 4), 0);
            ComputeBarrier(cb, _voxelBuffer, (ulong)(AggregateCells(2) * 4), VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader,
                VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            stamp();
        });
        TopoPush push = MakeTopoPush(topology, dim, n, Viewpoint.Whole);
        push.C = (uint)AggregateGrid(dim);
        for (long first = 0; first < n; first += _splatChunkNodes)
        {
            long count = Math.Min(_splatChunkNodes, n - first);
            push.A = (uint)first;
            push.B = (uint)count;
            RunSegment((cb, stamp) =>
            {
                BindTopoSet(cb, VkPipelineBindPoint.Compute);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _binPipeline);
                Push(cb, push);
                Dispatch(cb, count);
                stamp();
            });
        }

        if (bakedEdges > 0)
        {
            // Build-time edge bundling: sampled edges are added to the same voxel grid as density along their length.
            TopoPush edgePush = MakeTopoPush(topology, dim, n, Viewpoint.Whole);
            edgePush.C = (uint)AggregateGrid(dim);
            edgePush.Stride = (uint)Math.Max(1, n / bakedEdges);
            edgePush.A = 0;
            edgePush.B = (uint)bakedEdges;
            RunSegment((cb, stamp) =>
            {
                BindTopoSet(cb, VkPipelineBindPoint.Compute);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _edgeBinPipeline);
                Push(cb, edgePush);
                Dispatch(cb, bakedEdges);
                stamp();
            });
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Per-frame aggregate path: clear the screen grid, project every non-empty voxel into it, shade it, then draw sampled edges.
    /// Needs <see cref="BuildAggregate"/> first. Cost does not depend on the node count.
    /// </summary>
    public CellStats MeasureAggregate(Topology topology, int dim, long n, Viewpoint view, int edges, int warmup, int measured) =>
        Measure(warmup, measured, ["clear", "reproject", "shade", "edges"], () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            push.A = (uint)AggregateCells(dim);
            push.B = ScreenWidth;
            push.C = (uint)AggregateGrid(dim);
            return RunSegment((cb, stamp) =>
            {
                _deviceApi.vkCmdFillBuffer(cb, _screenBuffer, 0, (ulong)(ScreenCells * 4L), 0);
                ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader,
                    VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                BindTopoSet(cb, VkPipelineBindPoint.Compute);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _reprojectPipeline);
                Push(cb, push);
                Dispatch(cb, AggregateCells(dim));
                ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.FragmentShader,
                    VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead);
                stamp();
                DrawShadeAndEdges(cb, push, n, edges, stamp);
            });
        });

    /// <summary>Static camera: the screen grid is already built, so each frame only shades it and draws the sampled edges.</summary>
    public CellStats MeasureCachedShade(Topology topology, int dim, long n, Viewpoint view, int edges, int warmup, int measured)
    {
        // One reproject to fill the grid, outside the measured frames.
        MeasureAggregate(topology, dim, n, view, edges, 0, 1);
        return Measure(warmup, measured, ["shade", "edges"], () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            push.B = ScreenWidth;
            push.C = ScreenWidth;
            return RunSegment((cb, stamp) => DrawShadeAndEdges(cb, push, n, edges, stamp));
        });
    }

    private void DrawShadeAndEdges(VkCommandBuffer cb, TopoPush push, long n, int edges, Action stamp)
    {
        push.C = ScreenWidth;
        BeginTopoRendering(cb);
        BindTopoSet(cb, VkPipelineBindPoint.Graphics);
        Push(cb, push);
        _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _shadePipeline);
        _deviceApi.vkCmdDraw(cb, 3, 1, 0, 0);
        stamp();
        push.Stride = (uint)Math.Max(1, edges > 0 ? n / edges : 1);
        Push(cb, push);
        _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _edgesPipeline);
        _deviceApi.vkCmdDraw(cb, (uint)edges * 2, 1, 0, 0);
        stamp();
        _deviceApi.vkCmdEndRendering(cb);
    }

    /// <summary>
    /// Full per-frame splat: every node generated from its index and counted into the screen grid, in chunks of
    /// 128M nodes per submit, then shaded. This is the honest cost of per-node splatting at this size.
    /// </summary>
    public CellStats MeasureFullSplat(Topology topology, int dim, long n, Viewpoint view, int warmup, int measured)
    {
        var passes = new[] { "clear", "splat", "shade" };
        return Measure(warmup, measured, passes, () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            push.B = 0;
            push.C = ScreenWidth;
            double clear = RunSegment((cb, stamp) =>
            {
                _deviceApi.vkCmdFillBuffer(cb, _screenBuffer, 0, (ulong)(ScreenCells * 4L), 0);
                ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader,
                    VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
            })[0];
            double splat = 0;
            for (long first = 0; first < n; first += _splatChunkNodes)
            {
                long count = Math.Min(_splatChunkNodes, n - first);
                push.A = (uint)first;
                push.B = (uint)count;
                splat += RunSegment((cb, stamp) =>
                {
                    BindTopoSet(cb, VkPipelineBindPoint.Compute);
                    _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _splatPipeline);
                    Push(cb, push);
                    Dispatch(cb, count);
                    ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.FragmentShader | VkPipelineStageFlags.ComputeShader,
                        VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                    stamp();
                })[0];
            }

            double shade = RunSegment((cb, stamp) =>
            {
                BeginTopoRendering(cb);
                BindTopoSet(cb, VkPipelineBindPoint.Graphics);
                Push(cb, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _shadePipeline);
                _deviceApi.vkCmdDraw(cb, 3, 1, 0, 0);
                _deviceApi.vkCmdEndRendering(cb);
                stamp();
            })[0];
            return [clear, splat, shade];
        });
    }

    private static CellStats Measure(int warmup, int measured, string[] passNames, Func<double[]> frame)
    {
        for (int i = 0; i < warmup; i++)
        {
            frame();
        }

        var wall = new double[measured];
        var gpu = new double[measured];
        var perPass = passNames.Select(_ => new double[measured]).ToArray();
        for (int i = 0; i < measured; i++)
        {
            long start = Stopwatch.GetTimestamp();
            double[] passes = frame();
            wall[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            gpu[i] = passes.Sum();
            for (int p = 0; p < passNames.Length; p++)
            {
                perPass[p][i] = passes[p];
            }
        }

        var breakdown = new Dictionary<string, double>();
        for (int p = 0; p < passNames.Length; p++)
        {
            breakdown[passNames[p]] = Percentile(perPass[p], 0.5);
        }

        return new CellStats(measured, Percentile(gpu, 0.5), Percentile(gpu, 0.95), Percentile(gpu, 0.99),
            Percentile(wall, 0.5), Percentile(wall, 0.95), Percentile(wall, 0.99), wall.Max(), breakdown);
    }

    /// <summary>One CSV line. The first twelve columns match the node benchmark so the BenchGate reads it; the rest are extra.</summary>
    public static string CsvLine(string section, long totalNodes, long instances, double buildMs, long gpuBytes, int edges, CellStats s)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string passes = string.Join(';', s.PassP50Ms.Select(p => $"{p.Key}={p.Value.ToString("F4", c)}"));
        return string.Join(',', section, totalNodes, 0, instances, buildMs.ToString("F1", c), gpuBytes, "True",
            s.WallP50.ToString("F4", c), s.WallP95.ToString("F4", c), s.WallMax.ToString("F4", c), s.GpuP50.ToString("F4", c), s.GpuP95.ToString("F4", c),
            s.GpuP99.ToString("F4", c), s.WallP99.ToString("F4", c), (1000.0 / s.WallP50).ToString("F0", c), (1000.0 / s.WallP99).ToString("F0", c),
            edges, s.Frames, passes);
    }

    public const string CsvHeader =
        "section,total_nodes,resident_nodes,instances_drawn,upload_ms,resident_bytes,fits_budget,wall_p50_ms,wall_p95_ms,wall_max_ms,gpu_p50_ms,gpu_p95_ms," +
        "gpu_p99_ms,wall_p99_ms,fps_wall_p50,fps_wall_p99,edges_drawn,frames,pass_p50_ms";
}
