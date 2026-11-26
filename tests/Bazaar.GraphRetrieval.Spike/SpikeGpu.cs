using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>A device buffer with its memory.</summary>
internal readonly record struct GpuBuffer(VkBuffer Buffer, VkDeviceMemory Memory, ulong Size);

/// <summary>Minimal Vulkan compute runtime for the S28 spike: one queue, buffers, compute pipelines, timestamped submits.</summary>
internal sealed unsafe class SpikeGpu : IDisposable
{
    private VkInstance _instance;
    private VkInstanceApi _instanceApi = null!;
    private VkPhysicalDevice _physical;
    private VkDevice _device;
    private VkQueue _queue;
    private uint _family;
    private VkPhysicalDeviceMemoryProperties _memory;
    private VkCommandPool _pool;
    private VkQueryPool _queries;
    private float _timestampPeriodNs;
    private long _allocatedBytes;

    public VkDeviceApi Api { get; private set; } = null!;

    public string DeviceName { get; private set; } = string.Empty;

    public long DeviceLocalBytes { get; private set; }

    /// <summary>Bytes of device memory this process allocated through <see cref="CreateBuffer"/>.</summary>
    public long AllocatedBytes => _allocatedBytes;

    public static SpikeGpu Create()
    {
        var gpu = new SpikeGpu();
        gpu.Initialize();
        return gpu;
    }

    private void Initialize()
    {
        vkInitialize(null);
        var app = new VkApplicationInfo { sType = VkStructureType.ApplicationInfo, apiVersion = new VkVersion(1, 3, 0) };
        var instanceInfo = new VkInstanceCreateInfo { sType = VkStructureType.InstanceCreateInfo, pApplicationInfo = &app };
        Check(vkCreateInstance(&instanceInfo, null, out _instance), "vkCreateInstance");
        _instanceApi = new VkInstanceApi(in _instance);
        Check(_instanceApi.vkEnumeratePhysicalDevices(out uint count), "vkEnumeratePhysicalDevices");
        var devices = new VkPhysicalDevice[count];
        _instanceApi.vkEnumeratePhysicalDevices(devices);
        _physical = devices[0];
        foreach (VkPhysicalDevice device in devices)
        {
            _instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties candidate);
            if (candidate.deviceType == VkPhysicalDeviceType.DiscreteGpu)
            {
                _physical = device;
                break;
            }
        }

        _instanceApi.vkGetPhysicalDeviceProperties(_physical, out VkPhysicalDeviceProperties properties);
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(_physical, out _memory);
        DeviceName = Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? string.Empty;
        _timestampPeriodNs = properties.limits.timestampPeriod;
        for (uint i = 0; i < _memory.memoryHeapCount; i++)
        {
            if ((_memory.memoryHeaps[(int)i].flags & VkMemoryHeapFlags.DeviceLocal) != 0)
            {
                DeviceLocalBytes = Math.Max(DeviceLocalBytes, (long)_memory.memoryHeaps[(int)i].size);
            }
        }

        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(_physical, out uint familyCount);
        var families = new VkQueueFamilyProperties[familyCount];
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(_physical, families);
        _family = (uint)Array.FindIndex(families, f => (f.queueFlags & VkQueueFlags.Compute) != 0);
        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo { sType = VkStructureType.DeviceQueueCreateInfo, queueFamilyIndex = _family, queueCount = 1, pQueuePriorities = &priority };
        var deviceInfo = new VkDeviceCreateInfo { sType = VkStructureType.DeviceCreateInfo, queueCreateInfoCount = 1, pQueueCreateInfos = &queueInfo };
        Check(_instanceApi.vkCreateDevice(_physical, &deviceInfo, null, out _device), "vkCreateDevice");
        Api = new VkDeviceApi(_instanceApi, in _device);
        Api.vkGetDeviceQueue(_family, 0, out _queue);
        var poolInfo = new VkCommandPoolCreateInfo { sType = VkStructureType.CommandPoolCreateInfo, flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = _family };
        Check(Api.vkCreateCommandPool(&poolInfo, null, out _pool), "vkCreateCommandPool");
        var queryInfo = new VkQueryPoolCreateInfo { sType = VkStructureType.QueryPoolCreateInfo, queryType = VkQueryType.Timestamp, queryCount = 2 };
        Check(Api.vkCreateQueryPool(&queryInfo, null, out _queries), "vkCreateQueryPool");
    }

    /// <summary>Creates a buffer. Device-local by default; <paramref name="host"/> makes it host-visible, coherent and mapped.</summary>
    public GpuBuffer CreateBuffer(ulong size, bool host = false, VkBufferUsageFlags extra = 0)
    {
        var info = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst | extra,
            sharingMode = VkSharingMode.Exclusive,
        };
        Check(Api.vkCreateBuffer(&info, null, out VkBuffer buffer), "vkCreateBuffer");
        Api.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        VkMemoryPropertyFlags wanted = host ? VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent : VkMemoryPropertyFlags.DeviceLocal;
        uint type = uint.MaxValue;
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((requirements.memoryTypeBits & (1u << (int)i)) != 0 && (_memory.memoryTypes[(int)i].propertyFlags & wanted) == wanted)
            {
                type = i;
                break;
            }
        }

        if (type == uint.MaxValue)
        {
            throw new InvalidOperationException($"No memory type for {wanted}.");
        }

        var allocate = new VkMemoryAllocateInfo { sType = VkStructureType.MemoryAllocateInfo, allocationSize = requirements.size, memoryTypeIndex = type };
        VkResult result = Api.vkAllocateMemory(&allocate, null, out VkDeviceMemory memory);
        if (result != VkResult.Success)
        {
            Api.vkDestroyBuffer(buffer, null);
            throw new OutOfMemoryException($"vkAllocateMemory({size} bytes) failed with {result}.");
        }

        Check(Api.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory");
        if (!host)
        {
            _allocatedBytes += (long)requirements.size;
        }

        return new GpuBuffer(buffer, memory, size);
    }

    public void Free(GpuBuffer buffer, bool host = false)
    {
        Api.vkDeviceWaitIdle();
        Api.vkDestroyBuffer(buffer.Buffer, null);
        Api.vkFreeMemory(buffer.Memory, null);
        if (!host)
        {
            _allocatedBytes -= (long)buffer.Size;
        }
    }

    public void* Map(GpuBuffer buffer)
    {
        void* mapped;
        Check(Api.vkMapMemory(buffer.Memory, 0, buffer.Size, 0, &mapped), "vkMapMemory");
        return mapped;
    }

    public VkShaderModule LoadShader(string name)
    {
        byte[] code = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", name + ".spv"));
        fixed (byte* pointer = code)
        {
            var info = new VkShaderModuleCreateInfo { sType = VkStructureType.ShaderModuleCreateInfo, codeSize = (nuint)code.Length, pCode = (uint*)pointer };
            Check(Api.vkCreateShaderModule(&info, null, out VkShaderModule module), $"vkCreateShaderModule({name})");
            return module;
        }
    }

    /// <summary>A pipeline layout with one set of <paramref name="bindings"/> storage buffers and a 16-byte push block (compute only).</summary>
    public (VkDescriptorSetLayout SetLayout, VkPipelineLayout Layout) CreateLayout(int bindings, uint pushBytes = 16, int extraSets = 0)
    {
        var binds = stackalloc VkDescriptorSetLayoutBinding[bindings];
        for (uint b = 0; b < bindings; b++)
        {
            binds[b] = new VkDescriptorSetLayoutBinding { binding = b, descriptorType = VkDescriptorType.StorageBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlags.Compute };
        }

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = (uint)bindings, pBindings = binds };
        Check(Api.vkCreateDescriptorSetLayout(&layoutInfo, null, out VkDescriptorSetLayout setLayout), "vkCreateDescriptorSetLayout");
        var push = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = pushBytes };
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &push,
        };
        Check(Api.vkCreatePipelineLayout(&pipelineLayoutInfo, null, out VkPipelineLayout layout), "vkCreatePipelineLayout");
        return (setLayout, layout);
    }

    public VkPipeline CreatePipeline(VkShaderModule module, VkPipelineLayout layout, uint? specialization = null)
    {
        byte* entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        uint value = specialization ?? 0;
        var entryInfo = new VkSpecializationMapEntry { constantID = 0, offset = 0, size = sizeof(uint) };
        var specInfo = new VkSpecializationInfo { mapEntryCount = 1, pMapEntries = &entryInfo, dataSize = sizeof(uint), pData = &value };
        var info = new VkComputePipelineCreateInfo
        {
            sType = VkStructureType.ComputePipelineCreateInfo,
            stage = new VkPipelineShaderStageCreateInfo
            {
                sType = VkStructureType.PipelineShaderStageCreateInfo,
                stage = VkShaderStageFlags.Compute,
                module = module,
                pName = entry,
                pSpecializationInfo = specialization.HasValue ? &specInfo : null,
            },
            layout = layout,
        };
        VkPipeline pipeline;
        Check(Api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateComputePipelines");
        return pipeline;
    }

    /// <summary>Allocates one descriptor set from a fresh pool for <paramref name="buffers"/> bound whole, in binding order.</summary>
    public VkDescriptorSet CreateSet(VkDescriptorSetLayout layout, params GpuBuffer[] buffers)
    {
        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = (uint)buffers.Length };
        var poolInfo = new VkDescriptorPoolCreateInfo { sType = VkStructureType.DescriptorPoolCreateInfo, maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };
        Check(Api.vkCreateDescriptorPool(&poolInfo, null, out VkDescriptorPool pool), "vkCreateDescriptorPool");
        var allocate = new VkDescriptorSetAllocateInfo { sType = VkStructureType.DescriptorSetAllocateInfo, descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
        VkDescriptorSet set;
        Check(Api.vkAllocateDescriptorSets(&allocate, &set), "vkAllocateDescriptorSets");
        var infos = stackalloc VkDescriptorBufferInfo[buffers.Length];
        var writes = stackalloc VkWriteDescriptorSet[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            infos[i] = new VkDescriptorBufferInfo { buffer = buffers[i].Buffer, offset = 0, range = buffers[i].Size };
            writes[i] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = set,
                dstBinding = (uint)i,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &infos[i],
            };
        }

        Api.vkUpdateDescriptorSets((uint)buffers.Length, writes, 0, null);
        return set;
    }

    /// <summary>Records and submits one command buffer, waits for it and returns the GPU milliseconds between the first and last timestamp.</summary>
    public double Submit(Action<VkCommandBuffer> record)
    {
        var allocate = new VkCommandBufferAllocateInfo { sType = VkStructureType.CommandBufferAllocateInfo, commandPool = _pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        Check(Api.vkAllocateCommandBuffers(&allocate, &cb), "vkAllocateCommandBuffers");
        var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(Api.vkBeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer");
        Api.vkCmdResetQueryPool(cb, _queries, 0, 2);
        Api.vkCmdWriteTimestamp(cb, VkPipelineStageFlags.TopOfPipe, _queries, 0);
        record(cb);
        Api.vkCmdWriteTimestamp(cb, VkPipelineStageFlags.BottomOfPipe, _queries, 1);
        Check(Api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");
        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
        Check(Api.vkCreateFence(&fenceInfo, null, out VkFence fence), "vkCreateFence");
        var submit = new VkSubmitInfo { sType = VkStructureType.SubmitInfo, commandBufferCount = 1, pCommandBuffers = &cb };
        Check(Api.vkQueueSubmit(_queue, 1, &submit, fence), "vkQueueSubmit");
        Check(Api.vkWaitForFences(1, &fence, true, 120_000_000_000), "vkWaitForFences");
        ulong* ticks = stackalloc ulong[2];
        Check(Api.vkGetQueryPoolResults(_queries, 0, 2, 2 * sizeof(ulong), ticks, sizeof(ulong), VkQueryResultFlags.Bit64 | VkQueryResultFlags.Wait), "vkGetQueryPoolResults");
        Api.vkDestroyFence(fence, null);
        Api.vkFreeCommandBuffers(_pool, 1, &cb);
        return (ticks[1] - ticks[0]) * _timestampPeriodNs / 1_000_000.0;
    }

    public void Barrier(VkCommandBuffer cb)
    {
        var barrier = new VkMemoryBarrier
        {
            sType = VkStructureType.MemoryBarrier,
            srcAccessMask = VkAccessFlags.ShaderWrite | VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite | VkAccessFlags.TransferRead | VkAccessFlags.TransferWrite,
        };
        Api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.Transfer, default, 1, &barrier, 0, null, 0, null);
    }

    public void Dispatch(VkCommandBuffer cb, long groups, uint y = 1)
    {
        uint x = (uint)Math.Min(groups, 65535);
        uint rows = (uint)((groups + x - 1) / x);
        // A 2D dispatch is used for large row counts; shaders that take a query index in y use the 1D form (rows == 1).
        Api.vkCmdDispatch(cb, x, y == 1 ? rows : y, 1);
    }

    public void Copy(GpuBuffer source, GpuBuffer destination, ulong size, ulong sourceOffset = 0, ulong destinationOffset = 0) =>
        Submit(cb =>
        {
            var region = new VkBufferCopy { srcOffset = sourceOffset, dstOffset = destinationOffset, size = size };
            Api.vkCmdCopyBuffer(cb, source.Buffer, destination.Buffer, 1, &region);
        });

    public static void Check(VkResult result, string call)
    {
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"{call} failed with {result}.");
        }
    }

    public void Dispose()
    {
        Api.vkDeviceWaitIdle();
        Api.vkDestroyDevice();
        _instanceApi.vkDestroyInstance();
    }
}
