using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Overdraw decimation as a second pass over the commands of a <see cref="TileCullPass"/> (doc constellation-scale-and-lod, 10.9 and 10.11).
/// For each surviving tile, every in-view instance claims one cell of a screen grid (about 2x2 pixels per cell) and the instance with the
/// highest index in each cell is kept; one <c>vkCmdDrawIndirect</c> then draws only those. On the Arc B580 this is the piece that took all
/// 150 benchmark cells under the frame budget, including thin-curve topologies that put hundreds of points on each pixel.
/// <para>
/// Inputs: the cull pass command buffer (<see cref="TileCullPass.CommandBuffer"/>), and a buffer of instance positions (<c>vec4</c> per
/// instance, xyz used) indexed by the same instance numbers the tiles use. The vertex shader that draws the result reads
/// <c>survivors[8 + gl_InstanceIndex]</c> from <see cref="SurvivorBuffer"/> to find the instance to draw. The winner is the highest
/// instance index in a cell, not the most important node; sort instances by importance ascending inside a tile if that matters.
/// </para>
/// <para>Needs the same device features as <see cref="TileCullPass"/>. Call <see cref="Record"/> after <see cref="TileCullPass.RecordCull"/> and outside a rendering scope.</para>
/// </summary>
public sealed unsafe class TileDecimationPass : IDisposable
{
    /// <summary>Words before the first survivor in <see cref="SurvivorBuffer"/> (indirect command, candidate counter).</summary>
    public const int SurvivorHeaderWords = 8;

    private readonly VkDeviceApi _device;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private readonly int _maxTiles;
    private VkBuffer _cells;
    private VkDeviceMemory _cellsMemory;
    private VkBuffer _survivors;
    private VkDeviceMemory _survivorsMemory;
    private VkDescriptorSetLayout _setLayout;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private VkPipelineLayout _layout;
    private VkPipeline _claim;
    private VkPipeline _select;
    private bool _disposed;

    /// <param name="device">Device functions.</param>
    /// <param name="memory">Memory properties of the physical device.</param>
    /// <param name="commands">The tile cull command buffer and its size in bytes.</param>
    /// <param name="positions">Instance positions, <c>vec4</c> each, and the buffer size in bytes.</param>
    /// <param name="cellsX">Grid width in cells (screen width divided by 2, rounded up).</param>
    /// <param name="cellsY">Grid height in cells.</param>
    /// <param name="maxTiles">Capacity of the cull pass (its <see cref="TileCullPass.MaxTiles"/>); one workgroup is launched per possible command.</param>
    public TileDecimationPass(VkDeviceApi device, in VkPhysicalDeviceMemoryProperties memory, (VkBuffer Buffer, ulong Bytes) commands,
        (VkBuffer Buffer, ulong Bytes) positions, int cellsX, int cellsY, int maxTiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellsY, 1);
        _device = device;
        _memory = memory;
        _maxTiles = maxTiles;
        CellsX = cellsX;
        CellsY = cellsY;
        ulong cellBytes = (ulong)cellsX * (ulong)cellsY * 4;
        ulong survivorBytes = ((ulong)SurvivorHeaderWords + ((ulong)cellsX * (ulong)cellsY)) * 4;
        _cells = CreateBuffer(cellBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst, out _cellsMemory);
        _survivors = CreateBuffer(survivorBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.IndirectBuffer, out _survivorsMemory);
        SurvivorBytes = survivorBytes;
        CreateDescriptors(commands, positions, cellBytes, survivorBytes);
        CreatePipelines();
    }

    public int CellsX { get; }

    public int CellsY { get; }

    /// <summary>The survivor buffer: words 0 to 3 are the draw command, word 4 the in-view candidate count, survivors start at word <see cref="SurvivorHeaderWords"/>.</summary>
    public VkBuffer SurvivorBuffer => _survivors;

    public ulong SurvivorBytes { get; }

    /// <summary>Records the reset, the claim pass and the select pass, and the barrier before the indirect draw.</summary>
    public void Record(VkCommandBuffer commandBuffer, in Matrix4x4 viewProjection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint* header = stackalloc uint[8];
        header[0] = 1;
        for (int i = 1; i < 8; i++)
        {
            header[i] = 0;
        }

        _device.vkCmdUpdateBuffer(commandBuffer, _survivors, 0, 32, header);
        _device.vkCmdFillBuffer(commandBuffer, _cells, 0, (ulong)CellsX * (ulong)CellsY * 4, 0);
        MemoryBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        VkDescriptorSet set = _set;
        _device.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
        PushData push = new() { ViewProjection = viewProjection, CellsX = (uint)CellsX, CellsY = (uint)CellsY };
        _device.vkCmdPushConstants(commandBuffer, _layout, VkShaderStageFlags.Compute, 0, (uint)sizeof(PushData), &push);
        _device.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _claim);
        _device.vkCmdDispatch(commandBuffer, (uint)_maxTiles, 1, 1);
        MemoryBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        _device.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, _select);
        _device.vkCmdDispatch(commandBuffer, (uint)_maxTiles, 1, 1);
        MemoryBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader,
            VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead);
    }

    /// <summary>Records the single indirect draw of the survivors, inside a rendering scope with the caller's pipeline bound.</summary>
    public void RecordDraw(VkCommandBuffer commandBuffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _device.vkCmdDrawIndirect(commandBuffer, _survivors, 0, 1, 16);
    }

    /// <summary>
    /// CPU reference of the passes: the instance indices that survive, in ascending order. <paramref name="tiles"/> are the
    /// (first instance, count) ranges the cull pass kept; an instance is a candidate when it projects inside the view (clip w above zero,
    /// both normalised coordinates strictly inside -1 to 1) and the winner of a cell is the highest instance index that falls in it.
    /// </summary>
    public static int[] Decimate(ReadOnlySpan<Vector3> positions, ReadOnlySpan<(int First, int Count)> tiles, in Matrix4x4 viewProjection, int cellsX, int cellsY)
    {
        var winners = new Dictionary<int, int>();
        foreach ((int first, int count) in tiles)
        {
            for (int j = 0; j < count; j++)
            {
                int index = first + j;
                Vector4 clip = Vector4.Transform(new Vector4(positions[index], 1f), viewProjection);
                if (clip.W <= 0f)
                {
                    continue;
                }

                float nx = clip.X / clip.W;
                float ny = clip.Y / clip.W;
                if (MathF.Abs(nx) >= 1f || MathF.Abs(ny) >= 1f)
                {
                    continue;
                }

                int x = Math.Min((int)((nx * 0.5f + 0.5f) * cellsX), cellsX - 1);
                int y = Math.Min((int)((ny * 0.5f + 0.5f) * cellsY), cellsY - 1);
                int cell = (y * cellsX) + x;
                if (!winners.TryGetValue(cell, out int current) || index > current)
                {
                    winners[cell] = index;
                }
            }
        }

        int[] result = [.. winners.Values];
        Array.Sort(result);
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.vkDeviceWaitIdle();
        _device.vkDestroyPipeline(_claim, null);
        _device.vkDestroyPipeline(_select, null);
        _device.vkDestroyPipelineLayout(_layout, null);
        _device.vkDestroyDescriptorPool(_pool, null);
        _device.vkDestroyDescriptorSetLayout(_setLayout, null);
        _device.vkDestroyBuffer(_cells, null);
        _device.vkFreeMemory(_cellsMemory, null);
        _device.vkDestroyBuffer(_survivors, null);
        _device.vkFreeMemory(_survivorsMemory, null);
    }

    private void MemoryBarrier(VkCommandBuffer cb, VkPipelineStageFlags source, VkPipelineStageFlags destination, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
    {
        var barrier = new VkMemoryBarrier { sType = VkStructureType.MemoryBarrier, srcAccessMask = sourceAccess, dstAccessMask = destinationAccess };
        _device.vkCmdPipelineBarrier(cb, source, destination, default, 1, &barrier, 0, null, 0, null);
    }

    private VkBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, out VkDeviceMemory memory)
    {
        var info = new VkBufferCreateInfo { sType = VkStructureType.BufferCreateInfo, size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        Check(_device.vkCreateBuffer(&info, null, out VkBuffer buffer), "vkCreateBuffer(decimation)");
        _device.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
        uint type = uint.MaxValue;
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((requirements.memoryTypeBits & (1u << (int)i)) != 0 && (_memory.memoryTypes[(int)i].propertyFlags & VkMemoryPropertyFlags.DeviceLocal) == VkMemoryPropertyFlags.DeviceLocal)
            {
                type = i;
                break;
            }
        }

        if (type == uint.MaxValue)
        {
            throw new InvalidOperationException("No device-local memory type.");
        }

        var allocate = new VkMemoryAllocateInfo { sType = VkStructureType.MemoryAllocateInfo, allocationSize = requirements.size, memoryTypeIndex = type };
        Check(_device.vkAllocateMemory(&allocate, null, out memory), "vkAllocateMemory(decimation)");
        Check(_device.vkBindBufferMemory(buffer, memory, 0), "vkBindBufferMemory(decimation)");
        return buffer;
    }

    private void CreateDescriptors((VkBuffer Buffer, ulong Bytes) commands, (VkBuffer Buffer, ulong Bytes) positions, ulong cellBytes, ulong survivorBytes)
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[4];
        for (uint b = 0; b < 4; b++)
        {
            bindings[b] = new VkDescriptorSetLayoutBinding { binding = b, descriptorType = VkDescriptorType.StorageBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlags.Compute };
        }

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo { sType = VkStructureType.DescriptorSetLayoutCreateInfo, bindingCount = 4, pBindings = bindings };
        Check(_device.vkCreateDescriptorSetLayout(&layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout(decimation)");
        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageBuffer, descriptorCount = 4 };
        var poolInfo = new VkDescriptorPoolCreateInfo { sType = VkStructureType.DescriptorPoolCreateInfo, maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };
        Check(_device.vkCreateDescriptorPool(&poolInfo, null, out _pool), "vkCreateDescriptorPool(decimation)");
        VkDescriptorSetLayout setLayout = _setLayout;
        var allocate = new VkDescriptorSetAllocateInfo { sType = VkStructureType.DescriptorSetAllocateInfo, descriptorPool = _pool, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(_device.vkAllocateDescriptorSets(&allocate, &set), "vkAllocateDescriptorSets(decimation)");
        _set = set;
        var infos = stackalloc VkDescriptorBufferInfo[4];
        infos[0] = new VkDescriptorBufferInfo { buffer = commands.Buffer, offset = 0, range = commands.Bytes };
        infos[1] = new VkDescriptorBufferInfo { buffer = positions.Buffer, offset = 0, range = positions.Bytes };
        infos[2] = new VkDescriptorBufferInfo { buffer = _cells, offset = 0, range = cellBytes };
        infos[3] = new VkDescriptorBufferInfo { buffer = _survivors, offset = 0, range = survivorBytes };
        var writes = stackalloc VkWriteDescriptorSet[4];
        for (uint b = 0; b < 4; b++)
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

        _device.vkUpdateDescriptorSets(4, writes, 0, null);
    }

    private void CreatePipelines()
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
        Check(_device.vkCreatePipelineLayout(&layoutInfo, null, out _layout), "vkCreatePipelineLayout(decimation)");
        _claim = CreateComputePipeline("tiledecimate_claim.comp.spv");
        _select = CreateComputePipeline("tiledecimate_select.comp.spv");
    }

    private VkPipeline CreateComputePipeline(string resource)
    {
        byte[] code = LoadShader(resource);
        VkShaderModule module;
        fixed (byte* pointer = code)
        {
            var moduleInfo = new VkShaderModuleCreateInfo { sType = VkStructureType.ShaderModuleCreateInfo, codeSize = (nuint)code.Length, pCode = (uint*)pointer };
            Check(_device.vkCreateShaderModule(&moduleInfo, null, out module), "vkCreateShaderModule(decimation)");
        }

        IntPtr entry = Marshal.StringToHGlobalAnsi("main");
        try
        {
            var info = new VkComputePipelineCreateInfo
            {
                sType = VkStructureType.ComputePipelineCreateInfo,
                stage = new VkPipelineShaderStageCreateInfo { sType = VkStructureType.PipelineShaderStageCreateInfo, stage = VkShaderStageFlags.Compute, module = module, pName = (byte*)entry },
                layout = _layout,
            };
            VkPipeline pipeline;
            Check(_device.vkCreateComputePipelines(VkPipelineCache.Null, 1, &info, null, &pipeline), "vkCreateComputePipelines(decimation)");
            return pipeline;
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            _device.vkDestroyShaderModule(module, null);
        }
    }

    private static byte[] LoadShader(string name)
    {
        using Stream stream = typeof(TileDecimationPass).Assembly.GetManifestResourceStream($"Bazaar.Constellation.Vulkan.Shaders.{name}")
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
        public uint CellsX;
        public uint CellsY;
        public uint Pad0;
        public uint Pad1;
    }
}
