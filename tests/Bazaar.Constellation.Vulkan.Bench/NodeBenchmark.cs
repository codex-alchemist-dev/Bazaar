using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Bazaar.Constellation.Vulkan;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>
/// Offscreen instanced-draw benchmark for the Constellation node path (doc constellation-scale-and-lod, section 7).
/// Nodes live in a device-local storage buffer (16 bytes each); one instance per node is drawn with vkCmdDraw.
/// Frame time is wall clock per submitted frame; GPU time comes from timestamp queries.
/// This is not a swapchain: frames render into an offscreen image, so no window is needed.
/// </summary>
public sealed unsafe class NodeBenchmark : IDisposable
{
    /// <summary>Bytes per node in the storage buffer: vec4 (x, y, cluster, state).</summary>
    public const int NodeBytes = 16;

    /// <summary>Per-frame instance cap from the design budget (PC tier, section 6).</summary>
    public const int InstanceCap = 2_000_000;

    /// <summary>Resident budget for node data on the GPU (PC tier, 1.5 GiB).</summary>
    public const long ResidentBudgetBytes = 1536L * 1024 * 1024;

    private const uint _width = 1280;
    private const uint _height = 720;
    private const int _uploadChunkNodes = 4_000_000;
    private const ulong _fenceTimeoutNs = 20_000_000_000;

    private VkInstance _instance;
    private VkInstanceApi _instanceApi = null!;
    private VkPhysicalDevice _physical;
    private VkDevice _device;
    private VkDeviceApi _deviceApi = null!;
    private VkQueue _queue;
    private uint _queueFamily;
    private float _timestampPeriodNs;
    private VkPhysicalDeviceMemoryProperties _memory;
    private VkPhysicalDeviceProperties _properties;

    private VkCommandPool _commandPool;
    private VkCommandBuffer _commandBuffer;
    private VkFence _fence;
    private VkQueryPool _queryPool;
    private VkImage _image;
    private VkDeviceMemory _imageMemory;
    private VkImageView _imageView;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _descriptorPool;
    private VkDescriptorSet _descriptorSet;
    private VkPipelineLayout _pipelineLayout;
    private VkPipeline _pipeline;
    private VkShaderModule _vertexModule;
    private VkShaderModule _fragmentModule;

    private VkBuffer _nodeBuffer;
    private VkDeviceMemory _nodeMemory;
    private long _nodeCount;

    public string DeviceName { get; private set; } = string.Empty;
    public string DriverVersion { get; private set; } = string.Empty;
    public string ApiVersion { get; private set; } = string.Empty;
    public long DeviceLocalBytes { get; private set; }
    public bool MeshShaderExtension { get; private set; }

    /// <summary>Creates the instance, device, queue and offscreen target, and builds the pipeline.</summary>
    public void Initialize(string shaderDirectory)
    {
        vkInitialize(null);

        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.ApplicationInfo,
            apiVersion = new VkVersion(VulkanProbe.TargetApiVersion),
        };
        var instanceInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.InstanceCreateInfo,
            pApplicationInfo = &appInfo,
        };
        Check(vkCreateInstance(&instanceInfo, null, out _instance), "vkCreateInstance");
        _instanceApi = new VkInstanceApi(in _instance);

        SelectPhysicalDevice();
        CreateDevice();
        CreateCommandObjects();
        CreateOffscreenTarget();
        CreatePipeline(shaderDirectory);
    }

    /// <summary>Allocates a device-local node buffer of <paramref name="nodeCount"/> nodes and fills it with synthetic data.</summary>
    public (double UploadMs, long Bytes) UploadNodes(long nodeCount)
    {
        DestroyNodeBuffer();
        _nodeCount = nodeCount;
        long bytes = nodeCount * NodeBytes;

        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = (ulong)bytes,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&bufferInfo, null, out _nodeBuffer), "vkCreateBuffer(nodes)");
        AllocateAndBind(_nodeBuffer, VkMemoryPropertyFlags.DeviceLocal, out _nodeMemory);

        var stopwatch = Stopwatch.StartNew();
        ulong staging = CreateHostBuffer((ulong)Math.Min(bytes, _uploadChunkNodes * (long)NodeBytes), out VkBuffer stagingBuffer, out VkDeviceMemory stagingMemory);
        _ = staging;
        var random = new Random(14);
        for (long first = 0; first < nodeCount; first += _uploadChunkNodes)
        {
            int count = (int)Math.Min(_uploadChunkNodes, nodeCount - first);
            void* mapped;
            Check(_deviceApi.vkMapMemory(stagingMemory, 0, (ulong)count * NodeBytes, 0, &mapped), "vkMapMemory(staging)");
            var span = new Span<float>(mapped, count * 4);
            for (int i = 0; i < count; i++)
            {
                span[i * 4] = (float)(random.NextDouble() * 2.0 - 1.0);
                span[i * 4 + 1] = (float)(random.NextDouble() * 2.0 - 1.0);
                span[i * 4 + 2] = (float)(first + i) % 4096f;
                span[i * 4 + 3] = 0f;
            }
            _deviceApi.vkUnmapMemory(stagingMemory);
            CopyBuffer(stagingBuffer, _nodeBuffer, (ulong)count * NodeBytes);
        }
        stopwatch.Stop();

        _deviceApi.vkDestroyBuffer(stagingBuffer, null);
        _deviceApi.vkFreeMemory(stagingMemory, null);
        UpdateDescriptor();
        return (stopwatch.Elapsed.TotalMilliseconds, bytes);
    }

    /// <summary>Draws the first <paramref name="instanceCount"/> nodes for warm-up and measured frames.</summary>
    public FrameStats Measure(int instanceCount, int warmupFrames, int measuredFrames)
    {
        if (instanceCount < 1 || instanceCount > _nodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }

        for (int i = 0; i < warmupFrames; i++)
        {
            RenderFrame(instanceCount, out _, out _);
        }

        var wallMs = new double[measuredFrames];
        var gpuMs = new double[measuredFrames];
        for (int i = 0; i < measuredFrames; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            RenderFrame(instanceCount, out double gpu, out _);
            stopwatch.Stop();
            wallMs[i] = stopwatch.Elapsed.TotalMilliseconds;
            gpuMs[i] = gpu;
        }

        return new FrameStats(instanceCount, Percentile(wallMs, 0.5), Percentile(wallMs, 0.95), wallMs.Max(),
            Percentile(gpuMs, 0.5), Percentile(gpuMs, 0.95));
    }

    private void RenderFrame(int instanceCount, out double gpuMs, out bool ok)
    {
        ok = true;
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences");
        _deviceApi.vkResetFences(1, &fence);

        Check(_deviceApi.vkResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkBeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");
        _deviceApi.vkCmdResetQueryPool(_commandBuffer, _queryPool, 0, 2);
        _deviceApi.vkCmdWriteTimestamp(_commandBuffer, VkPipelineStageFlags.TopOfPipe, _queryPool, 0);

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
        _deviceApi.vkCmdBeginRendering(_commandBuffer, &renderingInfo);
        _deviceApi.vkCmdBindPipeline(_commandBuffer, VkPipelineBindPoint.Graphics, _pipeline);
        VkDescriptorSet set = _descriptorSet;
        _deviceApi.vkCmdBindDescriptorSets(_commandBuffer, VkPipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);

        var push = new PushConstants
        {
            Transform = new Vector4(1.0f, _width / (float)_height, 0f, 0f),
            Quad = new Vector4(0.0015f, 0f, 0f, 0f),
        };
        _deviceApi.vkCmdPushConstants(_commandBuffer, _pipelineLayout, VkShaderStageFlags.Vertex, 0, (uint)sizeof(PushConstants), &push);
        _deviceApi.vkCmdDraw(_commandBuffer, 6, (uint)instanceCount, 0, 0);
        _deviceApi.vkCmdEndRendering(_commandBuffer);

        _deviceApi.vkCmdWriteTimestamp(_commandBuffer, VkPipelineStageFlags.BottomOfPipe, _queryPool, 1);
        Check(_deviceApi.vkEndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(frame)");

        ulong* timestamps = stackalloc ulong[2];
        Check(_deviceApi.vkGetQueryPoolResults(_queryPool, 0, 2, 2 * sizeof(ulong), timestamps, sizeof(ulong),
            VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults");
        gpuMs = (timestamps[1] - timestamps[0]) * _timestampPeriodNs / 1_000_000.0;
    }

    private void SelectPhysicalDevice()
    {
        Check(_instanceApi.vkEnumeratePhysicalDevices(out uint count), "vkEnumeratePhysicalDevices(count)");
        var devices = new VkPhysicalDevice[count];
        _instanceApi.vkEnumeratePhysicalDevices(devices);

        VkPhysicalDevice chosen = devices[0];
        foreach (VkPhysicalDevice device in devices)
        {
            _instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties candidate);
            if (candidate.deviceType == VkPhysicalDeviceType.DiscreteGpu)
            {
                chosen = device;
                break;
            }
        }

        _physical = chosen;
        _instanceApi.vkGetPhysicalDeviceProperties(_physical, out _properties);
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(_physical, out _memory);
        VkPhysicalDeviceProperties props = _properties;
        DeviceName = Marshal.PtrToStringUTF8((nint)props.deviceName) ?? string.Empty;

        DriverVersion = VulkanProbe.VersionString((uint)_properties.driverVersion);
        ApiVersion = VulkanProbe.VersionString((uint)_properties.apiVersion);
        _timestampPeriodNs = _properties.limits.timestampPeriod;
        for (uint i = 0; i < _memory.memoryHeapCount; i++)
        {
            if ((_memory.memoryHeaps[(int)i].flags & VkMemoryHeapFlags.DeviceLocal) != 0)
            {
                DeviceLocalBytes = Math.Max(DeviceLocalBytes, (long)_memory.memoryHeaps[(int)i].size);
            }
        }
    }

    private void CreateDevice()
    {
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(_physical, out uint familyCount);
        var families = new VkQueueFamilyProperties[familyCount];
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(_physical, families);
        _queueFamily = (uint)Array.FindIndex(families, f => (f.queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) == (VkQueueFlags.Graphics | VkQueueFlags.Compute));

        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VkStructureType.DeviceQueueCreateInfo,
            queueFamilyIndex = _queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            sType = VkStructureType.PhysicalDeviceVulkan13Features,
            dynamicRendering = true,
            synchronization2 = true,
        };
        var deviceInfo = new VkDeviceCreateInfo
        {
            sType = VkStructureType.DeviceCreateInfo,
            pNext = &features13,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
        };
        Check(_instanceApi.vkCreateDevice(_physical, &deviceInfo, null, out _device), "vkCreateDevice");
        _deviceApi = new VkDeviceApi(_instanceApi, in _device);
        _deviceApi.vkGetDeviceQueue(_queueFamily, 0, out _queue);
    }

    private void CreateCommandObjects()
    {
        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.CommandPoolCreateInfo,
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = _queueFamily,
        };
        Check(_deviceApi.vkCreateCommandPool(&poolInfo, null, out _commandPool), "vkCreateCommandPool");

        var allocate = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            commandPool = _commandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };
        VkCommandBuffer commandBuffer;
        Check(_deviceApi.vkAllocateCommandBuffers(&allocate, &commandBuffer), "vkAllocateCommandBuffers");
        _commandBuffer = commandBuffer;

        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo, flags = VkFenceCreateFlags.Signaled };
        Check(_deviceApi.vkCreateFence(&fenceInfo, null, out _fence), "vkCreateFence");

        var queryInfo = new VkQueryPoolCreateInfo
        {
            sType = VkStructureType.QueryPoolCreateInfo,
            queryType = VkQueryType.Timestamp,
            queryCount = 2,
        };
        Check(_deviceApi.vkCreateQueryPool(&queryInfo, null, out _queryPool), "vkCreateQueryPool");
    }

    private void CreateOffscreenTarget()
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.R8G8B8A8Unorm,
            extent = new VkExtent3D(_width, _height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.ColorAttachment,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        Check(_deviceApi.vkCreateImage(&imageInfo, null, out _image), "vkCreateImage");
        _deviceApi.vkGetImageMemoryRequirements(_image, out VkMemoryRequirements requirements);
        _imageMemory = Allocate(requirements, VkMemoryPropertyFlags.DeviceLocal);
        Check(_deviceApi.vkBindImageMemory(_image, _imageMemory, 0), "vkBindImageMemory");

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = _image,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.R8G8B8A8Unorm,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        Check(_deviceApi.vkCreateImageView(&viewInfo, null, out _imageView), "vkCreateImageView");
    }

    private void CreatePipeline(string shaderDirectory)
    {
        _vertexModule = CreateShaderModule(Path.Combine(shaderDirectory, "nodes.vert.spv"));
        _fragmentModule = CreateShaderModule(Path.Combine(shaderDirectory, "nodes.frag.spv"));

        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.StorageBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex,
        };
        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = 1,
            pBindings = &binding,
        };
        Check(_deviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 1 };
        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VkStructureType.DescriptorPoolCreateInfo,
            maxSets = 1,
            poolSizeCount = 1,
            pPoolSizes = &poolSize,
        };
        Check(_deviceApi.vkCreateDescriptorPool(&poolInfo, null, out _descriptorPool), "vkCreateDescriptorPool");

        VkDescriptorSetLayout setLayout = _setLayout;
        var setAllocate = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = _descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &setLayout,
        };
        VkDescriptorSet set;
        Check(_deviceApi.vkAllocateDescriptorSets(&setAllocate, &set), "vkAllocateDescriptorSets");
        _descriptorSet = set;

        var pushRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.Vertex,
            offset = 0,
            size = (uint)sizeof(PushConstants),
        };
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange,
        };
        Check(_deviceApi.vkCreatePipelineLayout(&pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
        stages[0] = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Vertex,
            module = _vertexModule,
            pName = (byte*)Marshal.StringToHGlobalAnsi("main"),
        };
        stages[1] = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Fragment,
            module = _fragmentModule,
            pName = (byte*)Marshal.StringToHGlobalAnsi("main"),
        };

        var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VkStructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo
        {
            sType = VkStructureType.PipelineInputAssemblyStateCreateInfo,
            topology = VkPrimitiveTopology.TriangleList,
        };
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
        var multisample = new VkPipelineMultisampleStateCreateInfo
        {
            sType = VkStructureType.PipelineMultisampleStateCreateInfo,
            rasterizationSamples = VkSampleCountFlags.Count1,
        };
        var blendAttachment = new VkPipelineColorBlendAttachmentState
        {
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
        };
        var colorBlend = new VkPipelineColorBlendStateCreateInfo
        {
            sType = VkStructureType.PipelineColorBlendStateCreateInfo,
            attachmentCount = 1,
            pAttachments = &blendAttachment,
        };
        VkFormat colorFormat = VkFormat.R8G8B8A8Unorm;
        var renderingInfo = new VkPipelineRenderingCreateInfo
        {
            sType = VkStructureType.PipelineRenderingCreateInfo,
            colorAttachmentCount = 1,
            pColorAttachmentFormats = &colorFormat,
        };
        var pipelineInfo = new VkGraphicsPipelineCreateInfo
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
            layout = _pipelineLayout,
        };
        VkPipeline pipeline;
        Check(_deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &pipelineInfo, null, &pipeline), "vkCreateGraphicsPipelines");
        _pipeline = pipeline;
    }

    private void UpdateDescriptor()
    {
        var bufferInfo = new VkDescriptorBufferInfo { buffer = _nodeBuffer, offset = 0, range = (ulong)(_nodeCount * NodeBytes) };
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

    private void CopyBuffer(VkBuffer source, VkBuffer destination, ulong size)
    {
        VkFence fence = _fence;
        VkCommandBuffer commandBuffer = _commandBuffer;
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(_deviceApi.vkResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer(copy)");
        Check(_deviceApi.vkBeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer(copy)");
        var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = size };
        _deviceApi.vkCmdCopyBuffer(_commandBuffer, source, destination, 1, &region);
        Check(_deviceApi.vkEndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(copy)");
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        _deviceApi.vkResetFences(1, &fence);
        Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit(copy)");
        Check(_deviceApi.vkWaitForFences(1, &fence, true, _fenceTimeoutNs), "vkWaitForFences(copy)");
    }

    private ulong CreateHostBuffer(ulong size, out VkBuffer buffer, out VkDeviceMemory memory)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = VkBufferUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(_deviceApi.vkCreateBuffer(&info, null, out buffer), "vkCreateBuffer(staging)");
        AllocateAndBind(buffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out memory);
        return size;
    }

    private void AllocateAndBind(VkBuffer buffer, VkMemoryPropertyFlags flags, out VkDeviceMemory memory)
    {
        _deviceApi.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        memory = Allocate(requirements, flags);
        Check(_deviceApi.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
    }

    private VkDeviceMemory Allocate(VkMemoryRequirements requirements, VkMemoryPropertyFlags flags)
    {
        uint typeIndex = FindMemoryType(requirements.memoryTypeBits, flags);
        var info = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.MemoryAllocateInfo,
            allocationSize = requirements.size,
            memoryTypeIndex = typeIndex,
        };
        Check(_deviceApi.vkAllocateMemory(&info, null, out VkDeviceMemory memory), "vkAllocateMemory");
        return memory;
    }

    private uint FindMemoryType(uint typeBits, VkMemoryPropertyFlags flags)
    {
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            bool allowed = (typeBits & (1u << (int)i)) != 0;
            if (allowed && (_memory.memoryTypes[(int)i].propertyFlags & flags) == flags)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No memory type matches {flags}.");
    }

    private VkShaderModule CreateShaderModule(string path)
    {
        byte[] code = File.ReadAllBytes(path);
        fixed (byte* codePointer = code)
        {
            var info = new VkShaderModuleCreateInfo
            {
                sType = VkStructureType.ShaderModuleCreateInfo,
                codeSize = (nuint)code.Length,
                pCode = (uint*)codePointer,
            };
            Check(_deviceApi.vkCreateShaderModule(&info, null, out VkShaderModule module), $"vkCreateShaderModule({path})");
            return module;
        }
    }

    private void DestroyNodeBuffer()
    {
        if (_nodeBuffer.Handle != 0)
        {
            _deviceApi.vkDestroyBuffer(_nodeBuffer, null);
            _deviceApi.vkFreeMemory(_nodeMemory, null);
            _nodeBuffer = default;
            _nodeMemory = default;
        }
    }

    private static void Check(VkResult result, string call)
    {
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"{call} failed with {result}.");
        }
    }

    private static double Percentile(double[] values, double fraction)
    {
        double[] sorted = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public void Dispose()
    {
        DestroyNodeBuffer();
        _deviceApi.vkDeviceWaitIdle();
        _deviceApi.vkDestroyDevice();
        _instanceApi.vkDestroyInstance();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector4 Transform;
        public Vector4 Quad;
    }
}

/// <summary>One measured run at a given instance count.</summary>
public sealed record FrameStats(int Instances, double WallP50Ms, double WallP95Ms, double WallMaxMs, double GpuP50Ms, double GpuP95Ms);
