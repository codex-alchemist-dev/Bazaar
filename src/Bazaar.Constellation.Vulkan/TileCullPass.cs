using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>One tile the cull pass tests: a world-space box and the slice of the instance list it owns.</summary>
/// <param name="Min">Lower corner of the box that bounds every instance in the tile.</param>
/// <param name="Max">Upper corner of the box.</param>
/// <param name="FirstInstance">Index of the tile's first instance; the vertex shader sees it as <c>gl_InstanceIndex</c> (base of the slice).</param>
/// <param name="InstanceCount">Instances in the tile. Tiles with zero are skipped.</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct TileCullEntry(Vector3 Min, Vector3 Max, uint FirstInstance, uint InstanceCount);

/// <summary>
/// GPU tile culling with a single indirect-count draw (doc constellation-scale-and-lod, section 10.11).
/// Selection stays with the caller (ViewSelector, TileDrawPlanner): it decides which tiles exist and their instance ranges and uploads
/// them with <see cref="UploadTiles"/>. Each frame <see cref="RecordCull"/> tests every tile's box against the frustum on the GPU and
/// writes one indirect command per survivor; <see cref="RecordDraw"/> issues one <c>vkCmdDrawIndirectCount</c> for them.
/// <para>
/// The device must be created with Vulkan 1.2 <c>drawIndirectCount</c> and core <c>multiDrawIndirect</c> and
/// <c>drawIndirectFirstInstance</c> enabled. Compute and draw run in the caller's command buffer; the caller binds its own graphics
/// pipeline and descriptor sets before <see cref="RecordDraw"/>, and its vertex shader reads instances at <c>gl_InstanceIndex</c>.
/// </para>
/// <para>
/// Not included: the Hi-Z occlusion test (it measured near zero gain with point occluders, see 10.10 and 10.11; the reference is the
/// bench shader <c>tile_cull.comp</c>) and per-tile decimation. The tile buffer is host-visible; do not call
/// <see cref="UploadTiles"/> while a frame that reads it is in flight.
/// </para>
/// </summary>
public sealed unsafe class TileCullPass : IDisposable
{
    /// <summary>Bytes per tile entry in the GPU buffer.</summary>
    public const int EntryBytes = 32;

    /// <summary>Byte offset of the first indirect command; the draw count is the first word of the same buffer.</summary>
    public const ulong CommandOffset = 16;

    private const uint _commandStride = 16;

    private readonly VkDeviceApi _device;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private VkBuffer _tileBuffer;
    private VkDeviceMemory _tileMemory;
    private void* _tileMapped;
    private VkBuffer _commandBuffer;
    private VkDeviceMemory _commandMemory;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private VkPipelineLayout _layout;
    private VkPipeline _pipeline;
    private int _tileCount;
    private bool _disposed;

    /// <param name="device">Device functions of a device created with the features listed on the class.</param>
    /// <param name="memory">Memory properties of its physical device.</param>
    /// <param name="maxTiles">Capacity of the tile and command buffers.</param>
    public TileCullPass(VkDeviceApi device, in VkPhysicalDeviceMemoryProperties memory, int maxTiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTiles, 1);
        _device = device;
        _memory = memory;
        MaxTiles = maxTiles;
        CreateBuffers();
        CreateDescriptors();
        CreatePipeline();
    }

    public int MaxTiles { get; }

    /// <summary>The buffer holding the draw count (word 0) and, from <see cref="CommandOffset"/>, the commands. Readable for statistics.</summary>
    public VkBuffer CommandBuffer => _commandBuffer;

    /// <summary>Copies the tiles for the next frames. Returns the count stored.</summary>
    public int UploadTiles(ReadOnlySpan<TileCullEntry> tiles)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (tiles.Length > MaxTiles)
        {
            throw new ArgumentException($"{tiles.Length} tiles exceed the capacity of {MaxTiles}.", nameof(tiles));
        }

        // Entry layout in the buffer: min.xyz + firstInstance bits, max.xyz + instanceCount bits.
        var target = new Span<uint>(_tileMapped, tiles.Length * 8);
        for (int i = 0; i < tiles.Length; i++)
        {
            TileCullEntry t = tiles[i];
            int o = i * 8;
            target[o] = BitConverter.SingleToUInt32Bits(t.Min.X);
            target[o + 1] = BitConverter.SingleToUInt32Bits(t.Min.Y);
            target[o + 2] = BitConverter.SingleToUInt32Bits(t.Min.Z);
            target[o + 3] = t.FirstInstance;
            target[o + 4] = BitConverter.SingleToUInt32Bits(t.Max.X);
            target[o + 5] = BitConverter.SingleToUInt32Bits(t.Max.Y);
            target[o + 6] = BitConverter.SingleToUInt32Bits(t.Max.Z);
            target[o + 7] = t.InstanceCount;
        }

        _tileCount = tiles.Length;
        return _tileCount;
    }

    /// <summary>
    /// Records the reset and the cull dispatch, and the barrier that makes the commands visible to <see cref="RecordDraw"/>.
    /// Call outside a rendering scope.
    /// </summary>
    /// <param name="viewProjection">The camera matrix as the renderer pushes it (System.Numerics layout, <c>Vector4.Transform(v, m)</c> gives clip space).</param>
    /// <param name="verticesPerDraw">Vertices drawn per instance: 1 for points, 6 for quads.</param>
    public void RecordCull(VkCommandBuffer commandBuffer, in Matrix4x4 viewProjection, uint verticesPerDraw)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.vkCmdFillBuffer(commandBuffer, _commandBuffer, 0, 16, 0);
        MemoryBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        if (_tileCount > 0)
        {
            VkDescriptorSet set = _set;
            _device.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
            _device.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _pipeline);
            PushData push = new() { ViewProjection = viewProjection, TileCount = (uint)_tileCount, VertexCount = verticesPerDraw };
            _device.vkCmdPushConstants(commandBuffer, _layout, VkShaderStageFlags.Compute, 0, (uint)sizeof(PushData), &push);
            _device.vkCmdDispatch(commandBuffer, (uint)((_tileCount + 255) / 256), 1, 1);
        }

        MemoryBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader,
            VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead);
    }

    /// <summary>Records the single indirect-count draw. Call inside a rendering scope with the caller's pipeline and descriptors bound.</summary>
    public void RecordDraw(VkCommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.vkCmdDrawIndirectCount(commandBuffer, _commandBuffer, CommandOffset, _commandBuffer, 0, (uint)Math.Max(_tileCount, 1), _commandStride);
    }

    /// <summary>
    /// CPU reference of the shader's frustum test: false when all eight corners of the box lie outside the same clip plane
    /// (reversed or normal Z, depth range 0 to w).
    /// </summary>
    public static bool IsVisible(Vector3 min, Vector3 max, in Matrix4x4 viewProjection)
    {
        uint allOutside = 63;
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) != 0 ? max.X : min.X, (c & 2) != 0 ? max.Y : min.Y, (c & 4) != 0 ? max.Z : min.Z);
            Vector4 q = Vector4.Transform(new Vector4(corner, 1f), viewProjection);
            allOutside &= (q.X < -q.W ? 1u : 0u) | (q.X > q.W ? 2u : 0u) | (q.Y < -q.W ? 4u : 0u) | (q.Y > q.W ? 8u : 0u) | (q.Z < 0f ? 16u : 0u) | (q.Z > q.W ? 32u : 0u);
        }

        return allOutside == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.vkDeviceWaitIdle();
        _device.vkDestroyPipeline(_pipeline, null);
        _device.vkDestroyPipelineLayout(_layout, null);
        _device.vkDestroyDescriptorPool(_pool, null);
        _device.vkDestroyDescriptorSetLayout(_setLayout, null);
        _device.vkUnmapMemory(_tileMemory);
        _device.vkDestroyBuffer(_tileBuffer, null);
        _device.vkFreeMemory(_tileMemory, null);
        _device.vkDestroyBuffer(_commandBuffer, null);
        _device.vkFreeMemory(_commandMemory, null);
    }

    private void MemoryBarrier(VkCommandBuffer cb, VkPipelineStageFlags source, VkPipelineStageFlags destination, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
    {
        var barrier = new VkMemoryBarrier { sType = VkStructureType.MemoryBarrier, srcAccessMask = sourceAccess, dstAccessMask = destinationAccess };
        _device.vkCmdPipelineBarrier(cb, source, destination, default, 1, &barrier, 0, null, 0, null);
    }

    private void CreateBuffers()
    {
        ulong tileBytes = (ulong)MaxTiles * EntryBytes;
        _tileBuffer = CreateBuffer(tileBytes, VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out _tileMemory);
        void* mapped;
        Check(_device.vkMapMemory(_tileMemory, 0, tileBytes, 0, &mapped), "vkMapMemory(tiles)");
        _tileMapped = mapped;
        ulong commandBytes = CommandOffset + ((ulong)MaxTiles * _commandStride);
        _commandBuffer = CreateBuffer(commandBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc,
            VkMemoryPropertyFlags.DeviceLocal, out _commandMemory);
    }

    private VkBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties, out VkDeviceMemory memory)
    {
        var info = new VkBufferCreateInfo { sType = VkStructureType.BufferCreateInfo, size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        Check(_device.vkCreateBuffer(&info, null, out VkBuffer buffer), "vkCreateBuffer(tile cull)");
        _device.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        uint type = uint.MaxValue;
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((requirements.memoryTypeBits & (1u << (int)i)) != 0 && (_memory.memoryTypes[(int)i].propertyFlags & properties) == properties)
            {
                type = i;
                break;
            }
        }

        if (type == uint.MaxValue)
        {
            throw new InvalidOperationException($"No memory type matches {properties}.");
        }

        var allocate = new VkMemoryAllocateInfo { sType = VkStructureType.MemoryAllocateInfo, allocationSize = requirements.size, memoryTypeIndex = type };
        Check(_device.vkAllocateMemory(&allocate, null, out memory), "vkAllocateMemory(tile cull)");
        Check(_device.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory(tile cull)");
        return buffer;
    }

    private void CreateDescriptors()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[2];
        for (uint b = 0; b < 2; b++)
        {
            bindings[b] = new VkDescriptorSetLayoutBinding { binding = b, descriptorType = VkDescriptorType.StorageBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlags.Compute };
        }

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = 2, pBindings = bindings };
        Check(_device.vkCreateDescriptorSetLayout(&layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout(tile cull)");
        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 2 };
        var poolInfo = new VkDescriptorPoolCreateInfo { sType = VkStructureType.DescriptorPoolCreateInfo, maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };
        Check(_device.vkCreateDescriptorPool(&poolInfo, null, out _pool), "vkCreateDescriptorPool(tile cull)");
        VkDescriptorSetLayout setLayout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { sType = VkStructureType.DescriptorSetAllocateInfo, descriptorPool = _pool, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(_device.vkAllocateDescriptorSets(&allocate, &set), "vkAllocateDescriptorSets(tile cull)");
        _set = set;

        var infos = stackalloc VkDescriptorBufferInfo[2];
        infos[0] = new VkDescriptorBufferInfo { buffer = _tileBuffer, offset = 0, range = (ulong)MaxTiles * EntryBytes };
        infos[1] = new VkDescriptorBufferInfo { buffer = _commandBuffer, offset = 0, range = CommandOffset + ((ulong)MaxTiles * _commandStride) };
        var writes = stackalloc VkWriteDescriptorSet[2];
        for (uint b = 0; b < 2; b++)
        {
            writes[b] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = _set,
                dstBinding = b,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageBuffer,
                pBufferInfo = &infos[b],
            };
        }

        _device.vkUpdateDescriptorSets(2, writes, 0, null);
    }

    private void CreatePipeline()
    {
        var pushRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = (uint)sizeof(PushData) };
        VkDescriptorSetLayout setLayout = _setLayout;
        var layoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushRange,
        };
        Check(_device.vkCreatePipelineLayout(&layoutInfo, null, out _layout), "vkCreatePipelineLayout(tile cull)");

        byte[] code = LoadShader("tilecull.comp.spv");
        VkShaderModule module;
        fixed (byte* codePointer = code)
        {
            var moduleInfo = new VkShaderModuleCreateInfo { sType = VkStructureType.ShaderModuleCreateInfo, codeSize = (nuint)code.Length, pCode = (uint*)codePointer };
            Check(_device.vkCreateShaderModule(&moduleInfo, null, out module), "vkCreateShaderModule(tile cull)");
        }

        IntPtr entry = Marshal.StringToHGlobalAnsi("main");
        try
        {
            var pipelineInfo = new VkComputePipelineCreateInfo
            {
                sType = VkStructureType.ComputePipelineCreateInfo,
                stage = new VkPipelineShaderStageCreateInfo
                {
                    sType = VkStructureType.PipelineShaderStageCreateInfo,
                    stage = VkShaderStageFlags.Compute,
                    module = module,
                    pName = (byte*)entry,
                },
                layout = _layout,
            };
            VkPipeline pipeline;
            Check(_device.vkCreateComputePipelines(VkPipelineCache.Null, 1, &pipelineInfo, null, &pipeline), "vkCreateComputePipelines(tile cull)");
            _pipeline = pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            _device.vkDestroyShaderModule(module, null);
        }
    }

    private static byte[] LoadShader(string name)
    {
        using Stream stream = typeof(TileCullPass).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{name}")
            ?? throw new InvalidOperationException($"Embedded shader {name} is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Check(VkResult result, string call)
    {
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"{call} failed with {result}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushData
    {
        public Matrix4x4 ViewProjection;
        public uint TileCount;
        public uint VertexCount;
        public uint Pad0;
        public uint Pad1;
    }
}
