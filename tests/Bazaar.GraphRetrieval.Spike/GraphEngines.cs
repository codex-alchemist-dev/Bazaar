using System.Collections.Concurrent;
using System.Diagnostics;
using Vortice.Vulkan;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>Multi-core CPU breadth-first search over the CSR graph: parallel frontier, atomic visited bitset.</summary>
internal sealed class CpuGraphEngine(GraphData graph, int threads)
{
    private readonly int[] _visited = new int[(graph.Nodes + 31) / 32];

    /// <summary>Returns the per-level digests (level 0 = seeds). Levels beyond the last non-empty one are zero.</summary>
    public LevelDigest[] Expand(uint[] seeds, int hops, uint kindMask)
    {
        Array.Clear(_visited);
        var digests = new LevelDigest[hops + 1];
        var frontier = new List<uint>(seeds);
        foreach (uint s in seeds)
        {
            _visited[s >> 5] |= 1 << (int)(s & 31);
        }

        digests[0] = Digest(frontier);
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        for (int level = 1; level <= hops && frontier.Count > 0; level++)
        {
            var next = new ConcurrentBag<uint[]>();
            int blocks = (frontier.Count + 255) / 256;
            List<uint> current = frontier;
            Parallel.For(0, blocks, options, () => new List<uint>(), (b, _, local) =>
            {
                int first = b * 256;
                int last = Math.Min(current.Count, first + 256);
                for (int i = first; i < last; i++)
                {
                    uint u = current[i];
                    for (uint e = graph.Offsets[u]; e < graph.Offsets[u + 1]; e++)
                    {
                        if (((kindMask >> graph.Kind(e)) & 1) == 0)
                        {
                            continue;
                        }

                        uint t = graph.Cols[e];
                        int bit = 1 << (int)(t & 31);
                        if ((Volatile.Read(ref _visited[t >> 5]) & bit) != 0)
                        {
                            continue;
                        }

                        if ((Interlocked.Or(ref _visited[t >> 5], bit) & bit) == 0)
                        {
                            local.Add(t);
                        }
                    }
                }

                return local;
            }, local => next.Add(local.ToArray()));
            frontier = new List<uint>(next.Sum(a => a.Length));
            foreach (uint[] part in next)
            {
                frontier.AddRange(part);
            }

            digests[level] = Digest(frontier);
        }

        return digests;
    }

    private static LevelDigest Digest(List<uint> nodes)
    {
        uint sum = 0, xor = 0;
        foreach (uint n in nodes)
        {
            sum += n;
            xor ^= n;
        }

        return new LevelDigest((uint)nodes.Count, sum, xor);
    }
}

/// <summary>The graph resident on the GPU with the BFS kernels.</summary>
internal sealed unsafe class GpuGraphEngine : IDisposable
{
    private const uint Cap = 4_000_000;
    private const int MaxLevels = 34;

    private readonly SpikeGpu _gpu;
    private readonly GraphData _graph;
    private readonly GpuBuffer _offsets, _cols, _kinds, _visited, _reached, _state, _seeds, _check, _results, _resultsHost, _checkHost, _seedsStage;
    private readonly VkPipelineLayout _layout;
    private readonly VkDescriptorSet _set;
    private readonly VkPipeline _init, _expand, _next, _checkPipeline;
    private readonly uint* _seedsMapped;
    private readonly uint* _resultsMapped;
    private readonly uint* _checkMapped;
    private readonly long _before;

    public GpuGraphEngine(SpikeGpu gpu, GraphData graph)
    {
        _gpu = gpu;
        _graph = graph;
        _before = gpu.AllocatedBytes;
        _offsets = Upload(graph.Offsets, (ulong)(graph.Nodes + 1) * 4);
        _cols = Upload(graph.Cols, (ulong)graph.Edges * 4);
        _kinds = Upload(graph.Kinds, (ulong)graph.Kinds.Length * 4);
        _visited = gpu.CreateBuffer(((ulong)graph.Nodes + 31) / 32 * 4);
        _reached = gpu.CreateBuffer(Cap * 4);
        _state = gpu.CreateBuffer(64 * 4, extra: VkBufferUsageFlags.IndirectBuffer);
        _seeds = gpu.CreateBuffer(128UL * 32 * 4, host: true);
        _check = gpu.CreateBuffer(MaxLevels * 2 * 4);
        _results = gpu.CreateBuffer(128UL * 64 * 4);
        _resultsHost = gpu.CreateBuffer(128UL * 64 * 4, host: true);
        _checkHost = gpu.CreateBuffer(MaxLevels * 2 * 4, host: true);
        _seedsStage = _seeds;
        _seedsMapped = (uint*)gpu.Map(_seeds);
        _resultsMapped = (uint*)gpu.Map(_resultsHost);
        _checkMapped = (uint*)gpu.Map(_checkHost);
        (VkDescriptorSetLayout setLayout, _layout) = gpu.CreateLayout(8, 32);
        _set = gpu.CreateSet(setLayout, _offsets, _cols, _kinds, _visited, _reached, _state, _seeds, _check);
        _init = gpu.CreatePipeline(gpu.LoadShader("bfs_init.comp"), _layout);
        _expand = gpu.CreatePipeline(gpu.LoadShader("bfs_expand.comp"), _layout);
        _next = gpu.CreatePipeline(gpu.LoadShader("bfs_next.comp"), _layout);
        _checkPipeline = gpu.CreatePipeline(gpu.LoadShader("bfs_check.comp"), _layout);
    }

    /// <summary>Device memory this graph holds.</summary>
    public long GpuBytes => _gpu.AllocatedBytes - _before;

    public bool Overflowed { get; private set; }

    private GpuBuffer Upload(uint[] data, ulong bytes)
    {
        GpuBuffer buffer = _gpu.CreateBuffer(bytes);
        const ulong stageBytes = 256UL * 1024 * 1024;
        GpuBuffer stage = _gpu.CreateBuffer(Math.Min(stageBytes, bytes), host: true);
        uint* mapped = (uint*)_gpu.Map(stage);
        ulong done = 0;
        while (done < bytes)
        {
            ulong piece = Math.Min(Math.Min(stageBytes, bytes), bytes - done);
            new ReadOnlySpan<uint>(data, (int)(done / 4), (int)(piece / 4)).CopyTo(new Span<uint>(mapped, (int)(piece / 4)));
            _gpu.Copy(stage, buffer, piece, 0, done);
            done += piece;
        }

        _gpu.Free(stage, host: true);
        return buffer;
    }

    /// <summary>
    /// Runs the queries back to back in one submission (each: clear the visited bitset, seed, <paramref name="hops"/> levels).
    /// Returns the GPU milliseconds, the wall milliseconds and, per query, the reached counts per level.
    /// With <paramref name="verify"/> the per-level digests are also computed (one query at a time).
    /// </summary>
    public (double GpuMs, double WallMs, uint[][] LevelEnds, LevelDigest[]? Digest) Expand(uint[][] seedSets, int hops, uint kindMask, bool verify = false)
    {
        long wallStart = Stopwatch.GetTimestamp();
        int queries = seedSets.Length;
        for (int q = 0; q < queries; q++)
        {
            for (int i = 0; i < seedSets[q].Length; i++)
            {
                _seedsMapped[(q * 32) + i] = seedSets[q][i];
            }
        }

        double gpuMs = _gpu.Submit(cb =>
        {
            VkDescriptorSet set = _set;
            _gpu.Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
            for (int q = 0; q < queries; q++)
            {
                _gpu.Api.vkCmdFillBuffer(cb, _visited.Buffer, 0, _visited.Size, 0);
                if (verify)
                {
                    _gpu.Api.vkCmdFillBuffer(cb, _check.Buffer, 0, _check.Size, 0);
                }

                _gpu.Barrier(cb);
                PushAndBind(cb, _init, kindMask, 0, seedSets[q].Length, q * 32);
                _gpu.Api.vkCmdDispatch(cb, 1, 1, 1);
                _gpu.Barrier(cb);
                for (int level = 0; level < hops; level++)
                {
                    PushAndBind(cb, _expand, kindMask, level, seedSets[q].Length, q * 32);
                    _gpu.Api.vkCmdDispatchIndirect(cb, _state.Buffer, 12);
                    _gpu.Barrier(cb);
                    PushAndBind(cb, _next, kindMask, level, seedSets[q].Length, q * 32);
                    _gpu.Api.vkCmdDispatch(cb, 1, 1, 1);
                    _gpu.Barrier(cb);
                }

                if (verify)
                {
                    PushAndBind(cb, _checkPipeline, kindMask, hops + 1, seedSets[q].Length, q * 32);
                    _gpu.Api.vkCmdDispatch(cb, (uint)((Cap + 255) / 256), 1, 1);
                    _gpu.Barrier(cb);
                    var checkRegion = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = _check.Size };
                    _gpu.Api.vkCmdCopyBuffer(cb, _check.Buffer, _checkHost.Buffer, 1, &checkRegion);
                }

                var region = new VkBufferCopy { srcOffset = 0, dstOffset = (ulong)q * 64 * 4, size = 64 * 4 };
                _gpu.Api.vkCmdCopyBuffer(cb, _state.Buffer, _results.Buffer, 1, &region);
                _gpu.Barrier(cb);
            }

            var all = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = (ulong)queries * 64 * 4 };
            _gpu.Api.vkCmdCopyBuffer(cb, _results.Buffer, _resultsHost.Buffer, 1, &all);
            _gpu.Barrier(cb);
        });

        var levelEnds = new uint[queries][];
        for (int q = 0; q < queries; q++)
        {
            levelEnds[q] = new uint[hops + 1];
            for (int l = 0; l <= hops; l++)
            {
                levelEnds[q][l] = _resultsMapped[(q * 64) + 8 + l];
            }

            if (_resultsMapped[(q * 64) + 6] != 0)
            {
                Overflowed = true;
            }
        }

        LevelDigest[]? digest = null;
        if (verify && queries == 1)
        {
            digest = new LevelDigest[hops + 1];
            for (int l = 0; l <= hops; l++)
            {
                uint count = levelEnds[0][l] - (l == 0 ? 0 : levelEnds[0][l - 1]);
                digest[l] = new LevelDigest(count, _checkMapped[l * 2], _checkMapped[(l * 2) + 1]);
            }
        }

        double wallMs = (Stopwatch.GetTimestamp() - wallStart) * 1000.0 / Stopwatch.Frequency;
        return (gpuMs, wallMs, levelEnds, digest);
    }

    private void PushAndBind(VkCommandBuffer cb, VkPipeline pipeline, uint kindMask, int level, int seedCount, int seedOffset)
    {
        uint* push = stackalloc uint[8];
        push[0] = kindMask;
        push[1] = Cap;
        push[2] = (uint)level;
        push[3] = (uint)seedCount;
        push[4] = (uint)seedOffset;
        _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, pipeline);
        _gpu.Api.vkCmdPushConstants(cb, _layout, VkShaderStageFlags.Compute, 0, 32, push);
    }

    public void Dispose()
    {
        foreach (GpuBuffer b in new[] { _offsets, _cols, _kinds, _visited, _reached, _state, _check, _results })
        {
            _gpu.Free(b);
        }

        _gpu.Free(_seeds, host: true);
        _gpu.Free(_resultsHost, host: true);
        _gpu.Free(_checkHost, host: true);
        _ = _seedsStage;
        _ = _graph;
    }
}

/// <summary>Personalised PageRank over one graph: 20 fixed power iterations, pull form over the transposed CSR, float32.</summary>
internal sealed class CpuPpr(GraphData graph, uint[] inOffsets, uint[] inSources, float[] inCoefficients, int threads)
{
    public float[] Run(uint[] seeds, int iterations, float alpha)
    {
        int n = graph.Nodes;
        var r = new float[n];
        foreach (uint s in seeds)
        {
            r[s] += 1f / seeds.Length;
        }

        float[] x = (float[])r.Clone();
        float[] y = new float[n];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads };
        int blocks = (n + 4095) / 4096;
        for (int it = 0; it < iterations; it++)
        {
            float[] from = x, into = y;
            Parallel.For(0, blocks, options, b =>
            {
                int first = b * 4096;
                int last = Math.Min(n, first + 4096);
                for (int v = first; v < last; v++)
                {
                    float sum = 0;
                    for (uint e = inOffsets[v]; e < inOffsets[v + 1]; e++)
                    {
                        sum += inCoefficients[e] * from[inSources[e]];
                    }

                    into[v] = (alpha * r[v]) + ((1 - alpha) * sum);
                }
            });
            (x, y) = (y, x);
        }

        return x;
    }
}

/// <summary>Personalised PageRank on the GPU: the transposed graph and the iteration buffers stay resident, one submission per query.</summary>
internal sealed unsafe class GpuPpr : IDisposable
{
    private readonly SpikeGpu _gpu;
    private readonly GpuBuffer _inOff, _inSrc, _inCoef, _a, _b, _r, _rStage, _outHost;
    private readonly VkPipelineLayout _layout;
    private readonly VkDescriptorSet _setAb, _setBa;
    private readonly VkPipeline _iter;
    private readonly int _nodes;
    private readonly float* _rMapped;
    private readonly float* _outMapped;

    public GpuPpr(SpikeGpu gpu, int nodes, uint[] inOffsets, uint[] inSources, float[] inCoefficients)
    {
        _gpu = gpu;
        _nodes = nodes;
        _inOff = UploadArray(inOffsets);
        _inSrc = UploadArray(inSources);
        _inCoef = UploadFloat(inCoefficients);
        ulong bytes = (ulong)nodes * 4;
        _a = gpu.CreateBuffer(bytes);
        _b = gpu.CreateBuffer(bytes);
        _r = gpu.CreateBuffer(bytes);
        _rStage = gpu.CreateBuffer(bytes, host: true);
        _outHost = gpu.CreateBuffer(bytes, host: true);
        _rMapped = (float*)gpu.Map(_rStage);
        _outMapped = (float*)gpu.Map(_outHost);
        (VkDescriptorSetLayout setLayout, _layout) = gpu.CreateLayout(6, 32);
        _setAb = gpu.CreateSet(setLayout, _inOff, _inSrc, _inCoef, _a, _b, _r);
        _setBa = gpu.CreateSet(setLayout, _inOff, _inSrc, _inCoef, _b, _a, _r);
        _iter = gpu.CreatePipeline(gpu.LoadShader("ppr_iter.comp"), _layout);
    }

    private GpuBuffer UploadArray(uint[] data)
    {
        GpuBuffer stage = _gpu.CreateBuffer((ulong)data.Length * 4, host: true);
        new ReadOnlySpan<uint>(data).CopyTo(new Span<uint>(_gpu.Map(stage), data.Length));
        GpuBuffer device = _gpu.CreateBuffer((ulong)data.Length * 4);
        _gpu.Copy(stage, device, (ulong)data.Length * 4);
        _gpu.Free(stage, host: true);
        return device;
    }

    private GpuBuffer UploadFloat(float[] data)
    {
        GpuBuffer stage = _gpu.CreateBuffer((ulong)data.Length * 4, host: true);
        new ReadOnlySpan<float>(data).CopyTo(new Span<float>(_gpu.Map(stage), data.Length));
        GpuBuffer device = _gpu.CreateBuffer((ulong)data.Length * 4);
        _gpu.Copy(stage, device, (ulong)data.Length * 4);
        _gpu.Free(stage, host: true);
        return device;
    }

    /// <summary>Runs <paramref name="iterations"/> iterations (even), returns the scores, the GPU ms and the wall ms including the readback.</summary>
    public (float[] Scores, double GpuMs, double WallMs) Run(uint[] seeds, int iterations, float alpha)
    {
        long wallStart = Stopwatch.GetTimestamp();
        new Span<float>(_rMapped, _nodes).Clear();
        foreach (uint s in seeds)
        {
            _rMapped[s] += 1f / seeds.Length;
        }

        ulong bytes = (ulong)_nodes * 4;
        double gpuMs = _gpu.Submit(cb =>
        {
            var copyR = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = bytes };
            _gpu.Api.vkCmdCopyBuffer(cb, _rStage.Buffer, _r.Buffer, 1, &copyR);
            _gpu.Api.vkCmdCopyBuffer(cb, _rStage.Buffer, _a.Buffer, 1, &copyR);
            _gpu.Barrier(cb);
            _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _iter);
            uint* push = stackalloc uint[8];
            push[0] = (uint)_nodes;
            push[1] = BitConverter.SingleToUInt32Bits(alpha);
            _gpu.Api.vkCmdPushConstants(cb, _layout, VkShaderStageFlags.Compute, 0, 32, push);
            for (int it = 0; it < iterations; it++)
            {
                VkDescriptorSet set = it % 2 == 0 ? _setAb : _setBa;
                _gpu.Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
                _gpu.Api.vkCmdDispatch(cb, (uint)((_nodes + 255) / 256), 1, 1);
                _gpu.Barrier(cb);
            }

            // After an even number of iterations the latest scores are in buffer A.
            var copyOut = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = bytes };
            _gpu.Api.vkCmdCopyBuffer(cb, iterations % 2 == 0 ? _a.Buffer : _b.Buffer, _outHost.Buffer, 1, &copyOut);
            _gpu.Barrier(cb);
        });

        var scores = new float[_nodes];
        new ReadOnlySpan<float>(_outMapped, _nodes).CopyTo(scores);
        double wallMs = (Stopwatch.GetTimestamp() - wallStart) * 1000.0 / Stopwatch.Frequency;
        return (scores, gpuMs, wallMs);
    }

    public void Dispose()
    {
        foreach (GpuBuffer b in new[] { _inOff, _inSrc, _inCoef, _a, _b, _r })
        {
            _gpu.Free(b);
        }

        _gpu.Free(_rStage, host: true);
        _gpu.Free(_outHost, host: true);
    }
}
