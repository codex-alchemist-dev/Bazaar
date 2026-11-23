using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>What the cull pass reported for the last measured frame of a cell.</summary>
public sealed record CullCounters(uint NonEmptyTiles, uint FrustumTiles, uint DrawnTiles, uint Instances);

// GPU-driven node path (doc constellation-scale-and-lod, section 10.10): the sampled points are binned into tiles once, a compute
// pass culls tiles against the frustum (and optionally a Hi-Z pyramid built from the previous frame's depth) and writes one
// indirect draw command per surviving tile, and a single vkCmdDrawIndirectCount draws them.
public sealed unsafe partial class NodeBenchmark
{
    /// <summary>Tiles in the cull grid: 64 x 64 in 2D, 16 x 16 x 16 in 3D.</summary>
    public const int TileCount = 4096;

    private const int _cullMaxPoints = 2_100_000;
    private const int _hizLevels = 11;

    private static readonly uint _hizWords = HizWordCount();

    private VkDescriptorSetLayout _cullSetLayout;
    private VkDescriptorSetLayout _depthSetLayout;
    private VkDescriptorPool _cullPool;
    private VkDescriptorSet _cullSet;
    private VkDescriptorSet _depthSet;
    private VkPipelineLayout _cullLayout;
    private VkPipelineLayout _hizLayout;
    private VkPipeline _tileInitPipeline;
    private VkPipeline _tileCountPipeline;
    private VkPipeline _tileScanPipeline;
    private VkPipeline _tileScatterPipeline;
    private VkPipeline _tileCullPipeline;
    private VkPipeline _hizDown0Pipeline;
    private VkPipeline _hizDownPipeline;
    private VkPipeline _pointsCullPipeline;
    private VkPipeline _pointsCullDepthPipeline;
    private VkBuffer _tileCountBuffer;
    private VkBuffer _tileOffsetBuffer;
    private VkBuffer _tileCursorBuffer;
    private VkBuffer _tileBoundsBuffer;
    private VkBuffer _sortedBuffer;
    private VkBuffer _cmdBuffer;
    private VkBuffer _hizBuffer;
    private VkDeviceMemory[] _cullMemory = new VkDeviceMemory[7];
    private VkImage _depthImage;
    private VkDeviceMemory _depthMemory;
    private VkImageView _depthView;

    private static uint HizWordCount()
    {
        uint w = 640, h = 360, total = 0;
        for (int l = 0; l < _hizLevels; l++)
        {
            total += w * h;
            w = (w + 1) / 2;
            h = (h + 1) / 2;
        }

        return total;
    }

    /// <summary>Creates the tile, indirect-command and Hi-Z buffers, the depth image and the cull pipelines. Call after <see cref="InitializeTopology"/>.</summary>
    public void InitializeCull(string shaderDirectory)
    {
        ulong tileWords = (ulong)TileCount * 4;
        VkBuffer Make(ulong bytes, int slot, VkBufferUsageFlags extra = 0) => CreateStorageBuffer(bytes, out _cullMemory[slot], extra);
        _tileCountBuffer = Make(tileWords, 0);
        _tileOffsetBuffer = Make(tileWords + 16, 1);
        _tileCursorBuffer = Make(tileWords, 2);
        _tileBoundsBuffer = Make((ulong)TileCount * 6 * 4, 3);
        _sortedBuffer = Make((ulong)_cullMaxPoints * 4, 4);
        _cmdBuffer = Make(16 + ((ulong)TileCount * 16), 5, VkBufferUsageFlags.IndirectBuffer | VkBufferUsageFlags.TransferSrc);
        _hizBuffer = Make(_hizWords * 4UL, 6);

        var bindings = stackalloc VkDescriptorSetLayoutBinding[7];
        for (uint b = 0; b < 7; b++)
        {
            bindings[b] = new VkDescriptorSetLayoutBinding
            {
                binding = b,
                descriptorType = VkDescriptorType.StorageBuffer,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute | VkShaderStageFlags.Vertex,
            };
        }

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = 7, pBindings = bindings };
        Check(_deviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out _cullSetLayout), "vkCreateDescriptorSetLayout(cull)");
        var depthBinding = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.SampledImage,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Compute,
        };
        var depthLayoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = 1, pBindings = &depthBinding };
        Check(_deviceApi.vkCreateDescriptorSetLayout(&depthLayoutInfo, null, out _depthSetLayout), "vkCreateDescriptorSetLayout(depth)");

        var poolSizes = stackalloc VkDescriptorPoolSize[2];
        poolSizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 7 };
        poolSizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo { sType = VkStructureType.DescriptorPoolCreateInfo, maxSets = 2, poolSizeCount = 2, pPoolSizes = poolSizes };
        Check(_deviceApi.vkCreateDescriptorPool(&poolInfo, null, out _cullPool), "vkCreateDescriptorPool(cull)");
        var setLayouts = stackalloc VkDescriptorSetLayout[2];
        setLayouts[0] = _cullSetLayout;
        setLayouts[1] = _depthSetLayout;
        var sets = stackalloc VkDescriptorSet[2];
        var allocate = new VkDescriptorSetAllocateInfo { sType = VkStructureType.DescriptorSetAllocateInfo, descriptorPool = _cullPool, descriptorSetCount = 2, pSetLayouts = setLayouts };
        Check(_deviceApi.vkAllocateDescriptorSets(&allocate, sets), "vkAllocateDescriptorSets(cull)");
        _cullSet = sets[0];
        _depthSet = sets[1];

        CreateDepthImage();

        VkBuffer[] buffers = [_tileCountBuffer, _tileOffsetBuffer, _tileCursorBuffer, _tileBoundsBuffer, _sortedBuffer, _cmdBuffer, _hizBuffer];
        ulong[] sizes = [tileWords, tileWords + 16, tileWords, (ulong)TileCount * 6 * 4, (ulong)_cullMaxPoints * 4, 16 + ((ulong)TileCount * 16), _hizWords * 4UL];
        var infos = stackalloc VkDescriptorBufferInfo[7];
        var writes = stackalloc VkWriteDescriptorSet[8];
        for (uint b = 0; b < 7; b++)
        {
            infos[b] = new VkDescriptorBufferInfo { buffer = buffers[b], offset = 0, range = sizes[b] };
            writes[b] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = _cullSet,
                dstBinding = b,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &infos[b],
            };
        }

        var imageInfo = new VkDescriptorImageInfo { imageView = _depthView, imageLayout = VkImageLayout.DepthStencilReadOnlyOptimal };
        writes[7] = new VkWriteDescriptorSet
        {
            sType = VkStructureType.WriteDescriptorSet,
            dstSet = _depthSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.SampledImage,
            pImageInfo = &imageInfo,
        };
        _deviceApi.vkUpdateDescriptorSets(8, writes, 0, null);

        var pushRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment | VkShaderStageFlags.Compute,
            offset = 0,
            size = (uint)sizeof(TopoPush),
        };
        VkDescriptorSetLayout cullSetLayout = _cullSetLayout;
        var layoutCreate = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &cullSetLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange,
        };
        Check(_deviceApi.vkCreatePipelineLayout(&layoutCreate, null, out _cullLayout), "vkCreatePipelineLayout(cull)");
        layoutCreate.setLayoutCount = 2;
        layoutCreate.pSetLayouts = setLayouts;
        Check(_deviceApi.vkCreatePipelineLayout(&layoutCreate, null, out _hizLayout), "vkCreatePipelineLayout(hiz)");

        VkShaderModule Module(string name) => CreateShaderModule(Path.Combine(shaderDirectory, name + ".spv"));
        _tileInitPipeline = CreateCullCompute(Module("tile_init.comp"), _cullLayout);
        _tileCountPipeline = CreateCullCompute(Module("tile_count.comp"), _cullLayout);
        _tileScanPipeline = CreateCullCompute(Module("tile_scan.comp"), _cullLayout);
        _tileScatterPipeline = CreateCullCompute(Module("tile_scatter.comp"), _cullLayout);
        _tileCullPipeline = CreateCullCompute(Module("tile_cull.comp"), _cullLayout);
        _hizDownPipeline = CreateCullCompute(Module("hizdown.comp"), _cullLayout);
        _hizDown0Pipeline = CreateCullCompute(Module("hizdown0.comp"), _hizLayout);
        _pointsCullPipeline = CreateCullGraphics(Module("points_cull.vert"), Module("flat.frag"), false);
        _pointsCullDepthPipeline = CreateCullGraphics(Module("points_cull.vert"), Module("flat.frag"), true);
    }

    private void CreateDepthImage()
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.D32Sfloat,
            extent = new VkExtent3D(_width, _height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        Check(_deviceApi.vkCreateImage(&imageInfo, null, out _depthImage), "vkCreateImage(depth)");
        _deviceApi.vkGetImageMemoryRequirements(_depthImage, out VkMemoryRequirements requirements);
        _depthMemory = Allocate(requirements, VkMemoryPropertyFlags.DeviceLocal);
        Check(_deviceApi.vkBindImageMemory(_depthImage, _depthMemory, 0), "vkBindImageMemory(depth)");
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = _depthImage,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.D32Sfloat,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Depth, 0, 1, 0, 1),
        };
        Check(_deviceApi.vkCreateImageView(&viewInfo, null, out _depthView), "vkCreateImageView(depth)");
    }

    private VkPipeline CreateCullCompute(VkShaderModule module, VkPipelineLayout layout)
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
            layout = layout,
        };
        VkPipeline pipeline;
        Check(_deviceApi.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateComputePipelines(cull)");
        return pipeline;
    }

    private VkPipeline CreateCullGraphics(VkShaderModule vertex, VkShaderModule fragment, bool depth)
    {
        byte* entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Vertex, module = vertex, pName = entry };
        stages[1] = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Fragment, module = fragment, pName = entry };
        var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VkStructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { sType = VkStructureType.PipelineInputAssemblyStateCreateInfo, topology = VkPrimitiveTopology.PointList };
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
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
        };
        var colorBlend = new VkPipelineColorBlendStateCreateInfo { sType = VkStructureType.PipelineColorBlendStateCreateInfo, attachmentCount = 1, pAttachments = &attachment };
        var depthState = new VkPipelineDepthStencilStateCreateInfo
        {
            sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
            depthTestEnable = true,
            depthWriteEnable = true,
            depthCompareOp = VkCompareOp.GreaterOrEqual,
        };
        VkFormat colorFormat = VkFormat.R8G8B8A8Unorm;
        var rendering = new VkPipelineRenderingCreateInfo
        {
            sType = VkStructureType.PipelineRenderingCreateInfo,
            colorAttachmentCount = 1,
            pColorAttachmentFormats = &colorFormat,
            depthAttachmentFormat = depth ? VkFormat.D32Sfloat : VkFormat.Undefined,
        };
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
            pDepthStencilState = depth ? &depthState : null,
            pColorBlendState = &colorBlend,
            layout = _cullLayout,
        };
        VkPipeline pipeline;
        Check(_deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines(cull)");
        return pipeline;
    }

    private void BeginCullRendering(VkCommandBuffer commandBuffer, bool depth, bool keepColor)
    {
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = _imageView,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = keepColor ? VkAttachmentLoadOp.Load : VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = _depthView,
            imageLayout = VkImageLayout.DepthAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue(new VkClearDepthStencilValue(0f, 0)),
        };
        var renderingInfo = new VkRenderingInfo
        {
            sType = VkStructureType.RenderingInfo,
            renderArea = new VkRect2D(0, 0, _width, _height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
            pDepthAttachment = depth ? &depthAttachment : null,
        };
        _deviceApi.vkCmdBeginRendering(commandBuffer, &renderingInfo);
    }

    private void DepthBarrier(VkCommandBuffer cb, VkImageLayout oldLayout, VkImageLayout newLayout, VkPipelineStageFlags source, VkPipelineStageFlags destination,
        VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = sourceAccess,
            dstAccessMask = destinationAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = _depthImage,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Depth, 0, 1, 0, 1),
        };
        _deviceApi.vkCmdPipelineBarrier(cb, source, destination, default, 0, null, 0, null, 1, &barrier);
    }

    private void PushCull(VkCommandBuffer commandBuffer, VkPipelineLayout layout, TopoPush push) =>
        _deviceApi.vkCmdPushConstants(commandBuffer, layout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment | VkShaderStageFlags.Compute, 0, (uint)sizeof(TopoPush), &push);

    private void BindCullSet(VkCommandBuffer commandBuffer, VkPipelineBindPoint point)
    {
        VkDescriptorSet set = _cullSet;
        _deviceApi.vkCmdBindDescriptorSets(commandBuffer, point, _cullLayout, 0, 1, &set, 0, null);
    }

    /// <summary>
    /// Bins the <paramref name="points"/> sampled points into the tile grid (counts, bounds, prefix sum, scatter) and clears the Hi-Z
    /// pyramid. The result does not depend on the camera, so one build serves all three viewpoints. Returns the wall milliseconds.
    /// </summary>
    public double BuildTiles(Topology topology, int dim, long n, int points)
    {
        TopoPush push = MakeTopoPush(topology, dim, n, Viewpoint.Whole);
        push.Stride = (uint)Math.Max(1, n / points);
        push.B = (uint)points;
        var stopwatch = Stopwatch.StartNew();
        RunSegment((cb, stamp) =>
        {
            _deviceApi.vkCmdFillBuffer(cb, _hizBuffer, 0, _hizWords * 4UL, 0);
            GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            BindCullSet(cb, VkPipelineBindPoint.Compute);
            PushCull(cb, _cullLayout, push);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileInitPipeline);
            Dispatch(cb, TileCount);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileCountPipeline);
            Dispatch(cb, points);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileScanPipeline);
            _deviceApi.vkCmdDispatch(cb, 1, 1, 1);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileScatterPipeline);
            Dispatch(cb, points);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.VertexShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead);
            stamp();
        });
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The GPU-driven frame: reset, tile cull (frustum, and with <paramref name="hiz"/> an occlusion test against the previous frame's
    /// depth pyramid), one indirect-count draw of the surviving tiles, then the edge pass; with Hi-Z the frame also writes depth and
    /// rebuilds the pyramid. Needs <see cref="BuildTiles"/> for the same topology, dimension, N and point count.
    /// <paramref name="occluderPointSize"/> above 0 draws the points larger, which gives the Hi-Z test real occluders (validation).
    /// </summary>
    public (CellStats Stats, CullCounters Counters) MeasureCulled(Topology topology, int dim, long n, Viewpoint view, int points, int edges, bool hiz,
        int warmup, int measured, float lodMinPixels = 0f, float lodMaxPixels = 0f, float occluderPointSize = 0f)
    {
        if (!IndirectCountSupported)
        {
            throw new InvalidOperationException("vkCmdDrawIndirectCount is not supported on this device.");
        }

        string[] names = hiz ? ["reset", "cull", "points", "edges", "hiz"] : ["reset", "cull", "points", "edges"];
        CellStats stats = Measure(warmup, measured, names, () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            push.Stride = (uint)Math.Max(1, n / points);
            push.B = (uint)points;
            push.A = hiz ? 1u : 0u;
            if (occluderPointSize > 0f)
            {
                push.PointSize = occluderPointSize;
            }

            return RunSegment((cb, stamp) =>
            {
                uint* header = stackalloc uint[4];
                header[0] = 0;
                header[1] = 0;
                header[2] = 0;
                header[3] = 0;
                if (hiz)
                {
                    DepthBarrier(cb, VkImageLayout.Undefined, VkImageLayout.DepthAttachmentOptimal, VkPipelineStageFlags.ComputeShader,
                        VkPipelineStageFlags.EarlyFragmentTests | VkPipelineStageFlags.LateFragmentTests, 0, VkAccessFlags.DepthStencilAttachmentRead | VkAccessFlags.DepthStencilAttachmentWrite);
                }

                _deviceApi.vkCmdUpdateBuffer(cb, _cmdBuffer, 0, 16, header);
                GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                BindCullSet(cb, VkPipelineBindPoint.Compute);
                PushCull(cb, _cullLayout, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileCullPipeline);
                Dispatch(cb, TileCount);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader,
                    VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead);
                stamp();
                BeginCullRendering(cb, hiz, false);
                BindCullSet(cb, VkPipelineBindPoint.Graphics);
                PushCull(cb, _cullLayout, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, hiz ? _pointsCullDepthPipeline : _pointsCullPipeline);
                _deviceApi.vkCmdDrawIndirectCount(cb, _cmdBuffer, 16, _cmdBuffer, 0, TileCount, 16);
                if (hiz)
                {
                    _deviceApi.vkCmdEndRendering(cb);
                    stamp();
                    BeginCullRendering(cb, false, true);
                }
                else
                {
                    stamp();
                }

                BindTopoSet(cb, VkPipelineBindPoint.Graphics);
                DrawEdges(cb, push, n, edges, lodMinPixels, lodMaxPixels);
                _deviceApi.vkCmdEndRendering(cb);
                stamp();
                if (hiz)
                {
                    RecordHizBuild(cb, push);
                    stamp();
                }
            });
        });

        uint[] header = ReadCmdHeader();
        return (stats, new CullCounters(header[3], header[1], header[0], header[2]));
    }

    /// <summary>Depth image to pyramid: level 0 from the depth texture, then one 2x2 min reduction per level.</summary>
    private void RecordHizBuild(VkCommandBuffer cb, TopoPush push)
    {
        DepthBarrier(cb, VkImageLayout.DepthAttachmentOptimal, VkImageLayout.DepthStencilReadOnlyOptimal, VkPipelineStageFlags.LateFragmentTests,
            VkPipelineStageFlags.ComputeShader, VkAccessFlags.DepthStencilAttachmentWrite, VkAccessFlags.ShaderRead);
        var sets = stackalloc VkDescriptorSet[2];
        sets[0] = _cullSet;
        sets[1] = _depthSet;
        _deviceApi.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Compute, _hizLayout, 0, 2, sets, 0, null);
        PushCull(cb, _hizLayout, push);
        _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _hizDown0Pipeline);
        Dispatch(cb, 640L * 360);
        GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        BindCullSet(cb, VkPipelineBindPoint.Compute);
        _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _hizDownPipeline);
        uint srcOffset = 0, srcWidth = 640, srcHeight = 360;
        for (int level = 1; level < _hizLevels; level++)
        {
            uint width = (srcWidth + 1) / 2, height = (srcHeight + 1) / 2;
            TopoPush down = push;
            down.A = srcOffset;
            down.B = srcOffset + (srcWidth * srcHeight);
            down.C = width;
            down.Stride = height;
            down.Topo = srcWidth;
            down.Dim = srcHeight;
            PushCull(cb, _cullLayout, down);
            Dispatch(cb, (long)width * height);
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
            srcOffset = down.B;
            srcWidth = width;
            srcHeight = height;
        }
    }

    private uint[] ReadCmdHeader()
    {
        RunSegment((cb, stamp) =>
        {
            GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkAccessFlags.ShaderWrite, VkAccessFlags.TransferRead);
            var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = 16 };
            _deviceApi.vkCmdCopyBuffer(cb, _cmdBuffer, _hostReadback, 1, &region);
            GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, VkAccessFlags.TransferWrite, VkAccessFlags.HostRead);
            stamp();
        });
        return [_hostReadbackMapped[0], _hostReadbackMapped[1], _hostReadbackMapped[2], _hostReadbackMapped[3]];
    }

    public const string CullCsvHeader = CsvHeader + ",tiles_nonempty,tiles_frustum,tiles_drawn,instances_drawn_gpu";

    public static string CullCsvLine(string section, long totalNodes, long instances, double buildMs, int edges, CellStats s, CullCounters c)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        return string.Join(',', CsvLine(section, totalNodes, instances, buildMs, TopologyGpuBytes, edges, s),
            c.NonEmptyTiles.ToString(culture), c.FrustumTiles.ToString(culture), c.DrawnTiles.ToString(culture), c.Instances.ToString(culture));
    }
}
