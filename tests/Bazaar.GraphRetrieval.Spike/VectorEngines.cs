using System.Runtime.Intrinsics;
using Vortice.Vulkan;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>Exact multi-core CPU top-k over the fp32 values of the stored fp16 rows (the reference), queries blocked four at a time.</summary>
internal sealed class CpuVectorEngine(VectorData data, int threads)
{
    public int Threads { get; } = threads;

    public TopK[] Search(float[][] queries, int k)
    {
        int chunkCount = data.Chunks.Length;
        var results = new TopK[queries.Length];
        // Partition the row chunks over the threads in a fixed contiguous split, so results do not depend on timing.
        var partials = new List<(float Score, long Row)>[Threads][];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Threads };
        Parallel.For(0, Threads, options, t =>
        {
            int first = (int)((long)chunkCount * t / Threads);
            int last = (int)((long)chunkCount * (t + 1) / Threads);
            var heaps = new PriorityQueue<long, (float, long)>[queries.Length];
            for (int q = 0; q < queries.Length; q++)
            {
                heaps[q] = new PriorityQueue<long, (float, long)>();
            }

            for (int c = first; c < last; c++)
            {
                float[] chunk = data.Chunks[c];
                float[] inv = data.InverseNorm[c];
                int rows = inv.Length;
                for (int q0 = 0; q0 < queries.Length; q0 += 4)
                {
                    int group = Math.Min(4, queries.Length - q0);
                    for (int r = 0; r < rows; r++)
                    {
                        ReadOnlySpan<float> row = chunk.AsSpan(r * data.Dim, data.Dim);
                        for (int g = 0; g < group; g++)
                        {
                            float score = VectorData.Dot(row, queries[q0 + g]) * inv[r];
                            PriorityQueue<long, (float, long)> heap = heaps[q0 + g];
                            long globalRow = ((long)c * VectorData.RowsPerChunk) + r;
                            if (heap.Count < k)
                            {
                                heap.Enqueue(globalRow, (score, -globalRow));
                            }
                            else if (heap.TryPeek(out _, out (float, long) worst) && (score, -globalRow).CompareTo(worst) > 0)
                            {
                                heap.DequeueEnqueue(globalRow, (score, -globalRow));
                            }
                        }
                    }
                }
            }

            var mine = new List<(float Score, long Row)>[queries.Length];
            for (int q = 0; q < queries.Length; q++)
            {
                mine[q] = [];
                while (heaps[q].TryDequeue(out long row, out (float Score, long Neg) priority))
                {
                    mine[q].Add((priority.Score, row));
                }
            }

            partials[t] = mine;
        });

        for (int q = 0; q < queries.Length; q++)
        {
            var all = new List<(float Score, long Row)>();
            for (int t = 0; t < Threads; t++)
            {
                all.AddRange(partials[t][q]);
            }

            all.Sort(TopKTools.Compare);
            var top = all.Take(k).ToArray();
            results[q] = new TopK([.. top.Select(x => x.Row)], [.. top.Select(x => x.Score)]);
        }

        return results;
    }
}

/// <summary>One index on the GPU: fp16 rows, or int8 rows with per-row scale and fp16 rescore of the survivors on the host.</summary>
internal sealed unsafe class GpuVectorIndex : IDisposable
{
    private const int MaxQueries = 128;
    private const int Cap = 256;
    private const int CandStride = 1 + (2 * Cap);
    private const ulong ChunkBytes = 2UL * 1024 * 1024 * 1024;

    private readonly SpikeGpu _gpu;
    private readonly VectorData _data;
    private readonly bool _int8;
    private readonly List<(GpuBuffer Matrix, GpuBuffer Aux, int Rows, long Base, VkDescriptorSet Set)> _chunks = [];
    private readonly GpuBuffer _queryBuffer;
    private readonly GpuBuffer _scores;
    private readonly GpuBuffer _hist1;
    private readonly GpuBuffer _hist2;
    private readonly GpuBuffer _params;
    private readonly GpuBuffer _cand;
    private readonly GpuBuffer _readback;
    private readonly void* _queryMapped;
    private readonly uint* _readbackMapped;
    private readonly VkPipelineLayout _scoreLayout;
    private readonly VkPipelineLayout _selectLayout;
    private readonly VkPipeline _score;
    private readonly VkPipeline _hist1Pipeline;
    private readonly VkPipeline _scan1;
    private readonly VkPipeline _hist2Pipeline;
    private readonly VkPipeline _scan2;
    private readonly VkPipeline _gather;
    private readonly VkDescriptorSet _selectSet;
    private readonly int _rowsPerChunk;
    private readonly long _baseGpuBytes;
    private static readonly int PassMask = int.Parse(Environment.GetEnvironmentVariable("S28_PASSES") ?? "63");

    public GpuVectorIndex(SpikeGpu gpu, VectorData data, bool int8)
    {
        _gpu = gpu;
        _data = data;
        _int8 = int8;
        int dim = data.Dim;
        ulong rowBytes = (ulong)dim * (ulong)(int8 ? 1 : 2);
        _rowsPerChunk = (int)(ChunkBytes / rowBytes) & ~15;
        long before = gpu.AllocatedBytes;

        (VkDescriptorSetLayout scoreSetLayout, _scoreLayout) = gpu.CreateLayout(4);
        (VkDescriptorSetLayout selectSetLayout, _selectLayout) = gpu.CreateLayout(5);
        _queryBuffer = gpu.CreateBuffer((ulong)MaxQueries * (ulong)dim * 4, host: true);
        _queryMapped = gpu.Map(_queryBuffer);
        _scores = gpu.CreateBuffer((ulong)MaxQueries * (ulong)_rowsPerChunk * 4);
        _hist1 = gpu.CreateBuffer((ulong)MaxQueries * 4096 * 4);
        _hist2 = gpu.CreateBuffer((ulong)MaxQueries * 4096 * 4);
        _params = gpu.CreateBuffer((ulong)MaxQueries * 16);
        int chunkCount = (int)((data.Rows + _rowsPerChunk - 1) / _rowsPerChunk);
        _cand = gpu.CreateBuffer((ulong)chunkCount * MaxQueries * CandStride * 4);
        _readback = gpu.CreateBuffer(_cand.Size, host: true);
        _readbackMapped = (uint*)gpu.Map(_readback);
        _selectSet = gpu.CreateSet(selectSetLayout, _scores, _hist1, _hist2, _params, _cand);

        VkShaderModule scoreModule = gpu.LoadShader(int8 ? "score_int8.comp" : "score_fp16.comp");
        _score = gpu.CreatePipeline(scoreModule, _scoreLayout);
        _hist1Pipeline = gpu.CreatePipeline(gpu.LoadShader("hist1.comp"), _selectLayout);
        _scan1 = gpu.CreatePipeline(gpu.LoadShader("scan1.comp"), _selectLayout);
        _hist2Pipeline = gpu.CreatePipeline(gpu.LoadShader("hist2.comp"), _selectLayout);
        _scan2 = gpu.CreatePipeline(gpu.LoadShader("scan2.comp"), _selectLayout);
        _gather = gpu.CreatePipeline(gpu.LoadShader("gather.comp"), _selectLayout);

        // Upload the matrix in chunks through a bounded staging buffer.
        const int stageRows = 16384;
        GpuBuffer stage = gpu.CreateBuffer((ulong)stageRows * rowBytes, host: true);
        void* stageMapped = gpu.Map(stage);
        long uploaded = 0;
        while (uploaded < data.Rows)
        {
            int rows = (int)Math.Min(_rowsPerChunk, data.Rows - uploaded);
            GpuBuffer matrix = gpu.CreateBuffer((ulong)rows * rowBytes);
            GpuBuffer aux = gpu.CreateBuffer((ulong)rows * 4);
            var auxHost = new float[rows];
            for (int first = 0; first < rows; first += stageRows)
            {
                int count = Math.Min(stageRows, rows - first);
                FillStage(stageMapped, uploaded + first, count, auxHost, first);
                gpu.Copy(stage, matrix, (ulong)count * rowBytes, 0, (ulong)first * rowBytes);
            }

            GpuBuffer auxStage = gpu.CreateBuffer((ulong)rows * 4, host: true);
            auxHost.AsSpan().CopyTo(new Span<float>(gpu.Map(auxStage), rows));
            gpu.Copy(auxStage, aux, (ulong)rows * 4);
            gpu.Free(auxStage, host: true);
            VkDescriptorSet set = gpu.CreateSet(scoreSetLayout, matrix, aux, _queryBuffer, _scores);
            _chunks.Add((matrix, aux, rows, uploaded, set));
            uploaded += rows;
        }

        gpu.Free(stage, host: true);
        _baseGpuBytes = gpu.AllocatedBytes - before;
    }

    /// <summary>Device memory the index holds: matrix, per-row aux, scores and selection buffers.</summary>
    public long GpuBytes => _baseGpuBytes;

    /// <summary>Candidate lists that overflowed the per-chunk capacity in any search so far (should stay zero).</summary>
    public int Overflows { get; private set; }

    private void FillStage(void* target, long firstRow, int count, float[] aux, int auxOffset)
    {
        int dim = _data.Dim;
        if (!_int8)
        {
            var words = new Span<Half>(target, count * dim);
            for (int r = 0; r < count; r++)
            {
                ReadOnlySpan<float> row = _data.Row(firstRow + r);
                for (int d = 0; d < dim; d++)
                {
                    words[(r * dim) + d] = (Half)row[d];
                }

                aux[auxOffset + r] = _data.InvNorm(firstRow + r);
            }

            return;
        }

        var bytes = new Span<sbyte>(target, count * dim);
        for (int r = 0; r < count; r++)
        {
            ReadOnlySpan<float> row = _data.Row(firstRow + r);
            float max = 0;
            for (int d = 0; d < dim; d++)
            {
                max = Math.Max(max, Math.Abs(row[d]));
            }

            float scale = max / 127f;
            for (int d = 0; d < dim; d++)
            {
                bytes[(r * dim) + d] = (sbyte)Math.Clamp((int)MathF.Round(row[d] / scale), -127, 127);
            }

            // Scores are cosine of the stored row: dot of the int8 values times the scale, over the row norm.
            aux[auxOffset + r] = scale * _data.InvNorm(firstRow + r);
        }
    }

    /// <summary>Top-k for up to 128 queries. Returns the results, the GPU milliseconds and the wall milliseconds.</summary>
    public (TopK[] Results, double GpuMs, double WallMs) Search(float[][] queries, int k)
    {
        int q = queries.Length;
        int dim = _data.Dim;
        long wallStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < q; i++)
        {
            queries[i].AsSpan().CopyTo(new Span<float>((float*)_queryMapped + ((long)i * dim), dim));
        }

        uint kPrime = (uint)(_int8 ? (4 * k) + 16 : k + 16);
        uint candWords = (uint)(_chunks.Count * MaxQueries * CandStride);
        double gpuMs = _gpu.Submit(cb =>
        {
            _gpu.Api.vkCmdFillBuffer(cb, _cand.Buffer, 0, _cand.Size, 0);
            for (int c = 0; c < _chunks.Count; c++)
            {
                (GpuBuffer matrix, GpuBuffer aux, int rows, long chunkBase, VkDescriptorSet set) = _chunks[c];
                _gpu.Api.vkCmdFillBuffer(cb, _hist1.Buffer, 0, _hist1.Size, 0);
                _gpu.Api.vkCmdFillBuffer(cb, _hist2.Buffer, 0, _hist2.Size, 0);
                _gpu.Barrier(cb);
                VkDescriptorSet scoreSet = set;
                _gpu.Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Compute, _scoreLayout, 0, 1, &scoreSet, 0, null);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _score);
                uint* scorePush = stackalloc uint[4];
                scorePush[0] = (uint)rows;
                scorePush[1] = (uint)dim;
                scorePush[2] = (uint)q;
                scorePush[3] = (uint)_rowsPerChunk;
                _gpu.Api.vkCmdPushConstants(cb, _scoreLayout, VkShaderStageFlags.Compute, 0, 16, scorePush);
                if ((PassMask & 1) != 0)
                {
                    _gpu.Dispatch(cb, (rows + 127) / 128);
                }

                _gpu.Barrier(cb);

                VkDescriptorSet selectSet = _selectSet;
                _gpu.Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Compute, _selectLayout, 0, 1, &selectSet, 0, null);
                uint* selectPush = stackalloc uint[4];
                selectPush[0] = (uint)rows;
                selectPush[1] = (uint)_rowsPerChunk;
                selectPush[2] = kPrime;
                selectPush[3] = (uint)(c * MaxQueries * CandStride);
                _gpu.Api.vkCmdPushConstants(cb, _selectLayout, VkShaderStageFlags.Compute, 0, 16, selectPush);
                uint groups = (uint)((rows + 255) / 256);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _hist1Pipeline);
                if ((PassMask & 2) != 0)
                {
                    _gpu.Api.vkCmdDispatch(cb, groups, (uint)q, 1);
                }

                _gpu.Barrier(cb);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _scan1);
                _gpu.Api.vkCmdDispatch(cb, (uint)q, 1, 1);
                _gpu.Barrier(cb);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _hist2Pipeline);
                if ((PassMask & 8) != 0)
                {
                    _gpu.Api.vkCmdDispatch(cb, groups, (uint)q, 1);
                }

                _gpu.Barrier(cb);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _scan2);
                _gpu.Api.vkCmdDispatch(cb, (uint)q, 1, 1);
                _gpu.Barrier(cb);
                _gpu.Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _gather);
                if ((PassMask & 32) != 0)
                {
                    _gpu.Api.vkCmdDispatch(cb, groups, (uint)q, 1);
                }

                _gpu.Barrier(cb);
                _ = matrix;
                _ = aux;
                _ = chunkBase;
            }

            var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = (ulong)candWords * 4 };
            _gpu.Api.vkCmdCopyBuffer(cb, _cand.Buffer, _readback.Buffer, 1, &region);
            _gpu.Barrier(cb);
        });

        var results = new TopK[q];
        for (int i = 0; i < q; i++)
        {
            var all = new List<(float Score, long Row)>();
            for (int c = 0; c < _chunks.Count; c++)
            {
                uint* list = _readbackMapped + (((long)c * MaxQueries) + i) * CandStride;
                uint count = list[0];
                if (count > Cap)
                {
                    Overflows++;
                    count = Cap;
                }

                for (int j = 0; j < count; j++)
                {
                    all.Add((BitConverter.UInt32BitsToSingle(list[2 + (j * 2)]), _chunks[c].Base + list[1 + (j * 2)]));
                }
            }

            all.Sort(TopKTools.Compare);
            if (_int8)
            {
                // Rescore the first-pass survivors with the exact fp16 values and keep the best k.
                int keep = Math.Min(all.Count, (4 * k) + 16);
                var rescored = new List<(float Score, long Row)>(keep);
                for (int j = 0; j < keep; j++)
                {
                    long row = all[j].Row;
                    rescored.Add((VectorData.Dot(_data.Row(row), queries[i]) * _data.InvNorm(row), row));
                }

                rescored.Sort(TopKTools.Compare);
                all = rescored;
            }

            var top = all.Take(k).ToArray();
            results[i] = new TopK([.. top.Select(x => x.Row)], [.. top.Select(x => x.Score)]);
        }

        double wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - wallStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return (results, gpuMs, wallMs);
    }

    public void Dispose()
    {
        foreach ((GpuBuffer matrix, GpuBuffer aux, _, _, _) in _chunks)
        {
            _gpu.Free(matrix);
            _gpu.Free(aux);
        }

        _gpu.Free(_scores);
        _gpu.Free(_hist1);
        _gpu.Free(_hist2);
        _gpu.Free(_params);
        _gpu.Free(_cand);
        _gpu.Free(_readback, host: true);
        _gpu.Free(_queryBuffer, host: true);
    }
}
