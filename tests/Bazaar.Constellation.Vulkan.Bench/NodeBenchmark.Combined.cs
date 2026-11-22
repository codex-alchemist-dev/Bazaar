using System.Globalization;
using System.Numerics;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>Decimation counters of the combined frame: sampled points that were in view in the kept tiles, and the winners drawn.</summary>
public sealed record CombinedCounters(uint Candidates, uint Survivors)
{
    /// <summary>Sampled points per occupied 2x2-pixel cell: the overdraw estimate the density switch uses.</summary>
    public double PointsPerCell => Survivors == 0 ? 0 : Candidates / (double)Survivors;
}

// Section 10.11: tile cull, decimation of the surviving tiles and the density path in one frame.
public sealed unsafe partial class NodeBenchmark
{
    /// <summary>
    /// One frame: reset, tile cull, decimation over the kept tiles (claim and select, which also give the overdraw estimate), then either
    /// the decimated points (<paramref name="density"/> false) or the aggregate density path (true), then the LOD edge pass.
    /// The switch decision is made by the caller from <see cref="CombinedCounters.PointsPerCell"/> of a previous frame (read back once,
    /// the benchmark camera is static); both paths include the decimation passes so the estimate costs what it would cost every frame.
    /// Needs <see cref="BuildTiles"/> and, for density, <see cref="BuildAggregate"/> for the same topology, dimension and N.
    /// </summary>
    public (CellStats Stats, CullCounters Cull, CombinedCounters Combined) MeasureCombined(Topology topology, int dim, long n, Viewpoint view, int points,
        int edges, bool density, int warmup, int measured, float lodMinPixels = 0f, float lodMaxPixels = 0f)
    {
        string[] names = density
            ? ["reset", "cull", "claim", "select", "clear", "reproject", "shade", "edges"]
            : ["reset", "cull", "claim", "select", "points", "edges"];
        CellStats stats = Measure(warmup, measured, names, () =>
        {
            TopoPush push = MakeTopoPush(topology, dim, n, view);
            push.Stride = (uint)Math.Max(1, n / points);
            push.B = (uint)points;
            push.A = 0;
            return RunSegment((cb, stamp) =>
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
                stamp();
                BindCullSet(cb, VkPipelineBindPoint.Compute);
                PushCull(cb, _cullLayout, push);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _tileCullPipeline);
                Dispatch(cb, TileCount);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _claimTilesPipeline);
                _deviceApi.vkCmdDispatch(cb, TileCount, 1, 1);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _selectTilesPipeline);
                _deviceApi.vkCmdDispatch(cb, TileCount, 1, 1);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.VertexShader | VkPipelineStageFlags.Transfer,
                    VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead | VkAccessFlags.TransferWrite);
                stamp();
                if (!density)
                {
                    BeginCullRendering(cb, false, false);
                    BindCullSet(cb, VkPipelineBindPoint.Graphics);
                    PushCull(cb, _cullLayout, push);
                    _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pointsDecPipeline);
                    _deviceApi.vkCmdDrawIndirect(cb, _cullSurvivors, 0, 1, 16);
                    stamp();
                    BindTopoSet(cb, VkPipelineBindPoint.Graphics);
                    DrawEdges(cb, push, n, edges, lodMinPixels, lodMaxPixels);
                    _deviceApi.vkCmdEndRendering(cb);
                    stamp();
                    return;
                }

                _deviceApi.vkCmdFillBuffer(cb, _screenBuffer, 0, (ulong)(ScreenCells * 4L), 0);
                ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader,
                    VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
                stamp();
                push.A = (uint)AggregateCells(dim);
                push.B = ScreenWidth;
                push.C = (uint)AggregateGrid(dim);
                BindTopoSet(cb, VkPipelineBindPoint.Compute);
                _deviceApi.vkCmdBindPipeline(cb, VkPipelineBindPoint.Compute, _reprojectPipeline);
                Push(cb, push);
                Dispatch(cb, AggregateCells(dim));
                ComputeBarrier(cb, _screenBuffer, (ulong)(ScreenCells * 4L), VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.FragmentShader,
                    VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead);
                stamp();
                DrawShadeAndEdges(cb, push, n, edges, stamp, lodMinPixels, lodMaxPixels);
            });
        });

        uint[] words = ReadCmdHeader();
        return (stats, new CullCounters(words[3], words[1], words[0], words[2]), new CombinedCounters(words[8], words[5]));
    }

    /// <summary>
    /// Runs the library's <see cref="TileCullPass"/> on random boxes and cameras and compares the GPU draw count and instance total with
    /// the CPU reference (<see cref="TileCullPass.IsVisible"/>). Returns the number of mismatching trials out of <paramref name="trials"/>.
    /// </summary>
    public int CheckTileCullPass(int trials)
    {
        const int tiles = 3000;
        using var pass = new TileCullPass(_deviceApi, in _memory, 4096);
        var random = new Random(7);
        var entries = new TileCullEntry[tiles];
        uint first = 0;
        for (int i = 0; i < tiles; i++)
        {
            var centre = new Vector3((float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2));
            var half = new Vector3((float)(random.NextDouble() * 0.2), (float)(random.NextDouble() * 0.2), (float)(random.NextDouble() * 0.2));
            uint count = i % 11 == 0 ? 0u : (uint)random.Next(1, 500);
            entries[i] = new TileCullEntry(centre - half, centre + half, first, count);
            first += count;
        }

        pass.UploadTiles(entries);
        int bad = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), 2f + (float)random.NextDouble()),
                new Vector3((float)(random.NextDouble() - 0.5), (float)(random.NextDouble() - 0.5), 0f), Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(0.5f + (float)random.NextDouble(), 16f / 9f, 0.05f, 20f);
            Matrix4x4 viewProjection = view * projection;
            uint expectedDraws = 0, expectedInstances = 0;
            foreach (TileCullEntry e in entries)
            {
                if (e.InstanceCount > 0 && TileCullPass.IsVisible(e.Min, e.Max, viewProjection))
                {
                    expectedDraws++;
                    expectedInstances += e.InstanceCount;
                }
            }

            RunSegment((cb, stamp) =>
            {
                pass.RecordCull(cb, in viewProjection, 1);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkAccessFlags.ShaderWrite, VkAccessFlags.TransferRead);
                var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = 16 };
                _deviceApi.vkCmdCopyBuffer(cb, pass.CommandBuffer, _hostReadback, 1, &region);
                GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, VkAccessFlags.TransferWrite, VkAccessFlags.HostRead);
                stamp();
            });
            if (_hostReadbackMapped[0] != expectedDraws || _hostReadbackMapped[2] != expectedInstances)
            {
                bad++;
                Console.WriteLine($"tilecull-check trial {trial}: gpu draws/instances {_hostReadbackMapped[0]}/{_hostReadbackMapped[2]}, cpu {expectedDraws}/{expectedInstances}");
            }
        }

        return bad;
    }

    /// <summary>
    /// Runs the library <see cref="TileCullPass"/> followed by <see cref="TileDecimationPass"/> on random instances and cameras and compares the
    /// surviving instance set with the CPU reference. Returns the number of mismatching trials out of <paramref name="trials"/>.
    /// </summary>
    public int CheckTileDecimationPass(int trials)
    {
        const int tiles = 2000;
        const int perTile = 150;
        const int instances = tiles * perTile;
        var random = new Random(11);
        var positions = new Vector3[instances];
        var entries = new TileCullEntry[tiles];
        for (int t = 0; t < tiles; t++)
        {
            var centre = new Vector3((float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2), (float)(random.NextDouble() * 4 - 2));
            Vector3 min = centre, max = centre;
            for (int i = 0; i < perTile; i++)
            {
                var p = centre + new Vector3((float)(random.NextDouble() - 0.5) * 0.3f, (float)(random.NextDouble() - 0.5) * 0.3f, (float)(random.NextDouble() - 0.5) * 0.3f);
                positions[(t * perTile) + i] = p;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            entries[t] = new TileCullEntry(min, max, (uint)(t * perTile), t % 13 == 0 ? 0u : perTile);
        }

        ulong positionBytes = (ulong)instances * 16;
        var positionInfo = new VkBufferCreateInfo { sType = VkStructureType.BufferCreateInfo, size = positionBytes, usage = VkBufferUsageFlags.StorageBuffer, sharingMode = VkSharingMode.Exclusive };
        Check(_deviceApi.vkCreateBuffer(&positionInfo, null, out VkBuffer positionBuffer), "vkCreateBuffer(positions)");
        AllocateAndBind(positionBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out VkDeviceMemory positionMemory);
        void* positionMapped;
        Check(_deviceApi.vkMapMemory(positionMemory, 0, positionBytes, 0, &positionMapped), "vkMapMemory(positions)");
        var gpuPositions = new Span<Vector4>(positionMapped, instances);
        for (int i = 0; i < instances; i++)
        {
            gpuPositions[i] = new Vector4(positions[i], 0f);
        }

        ulong survivorBytes = (8UL + (640 * 360)) * 4;
        var readbackInfo = new VkBufferCreateInfo { sType = VkStructureType.BufferCreateInfo, size = survivorBytes, usage = VkBufferUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive };
        Check(_deviceApi.vkCreateBuffer(&readbackInfo, null, out VkBuffer readback), "vkCreateBuffer(survivor readback)");
        AllocateAndBind(readback, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, out VkDeviceMemory readbackMemory);
        void* readbackMapped;
        Check(_deviceApi.vkMapMemory(readbackMemory, 0, survivorBytes, 0, &readbackMapped), "vkMapMemory(survivor readback)");

        using var cull = new TileCullPass(_deviceApi, in _memory, 4096);
        cull.UploadTiles(entries);
        using var decimation = new TileDecimationPass(_deviceApi, in _memory, (cull.CommandBuffer, TileCullPass.CommandOffset + (4096UL * 16)), (positionBuffer, positionBytes), 640, 360, 4096);
        int bad = 0;
        int worst = 0;
        for (int trial = 0; trial < trials; trial++)
        {
            Matrix4x4 viewProjection;
            if (trial % 2 == 0)
            {
                float zoom = 0.6f + (float)random.NextDouble() * 3f;
                viewProjection = new Matrix4x4(zoom, 0, 0, 0, 0, zoom, 0, 0, 0, 0, 0, 0, 0, 0, 0.5f, 1);
            }
            else
            {
                Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), 2f + (float)random.NextDouble()), Vector3.Zero, Vector3.UnitY);
                viewProjection = view * Matrix4x4.CreatePerspectiveFieldOfView(0.5f + (float)random.NextDouble(), 16f / 9f, 0.05f, 20f);
            }

            var kept = new List<(int First, int Count)>();
            foreach (TileCullEntry e in entries)
            {
                if (e.InstanceCount > 0 && TileCullPass.IsVisible(e.Min, e.Max, viewProjection))
                {
                    kept.Add(((int)e.FirstInstance, (int)e.InstanceCount));
                }
            }

            int[] expected = TileDecimationPass.Decimate(positions, [.. kept], viewProjection, 640, 360);
            RunSegment((cb, stamp) =>
            {
                cull.RecordCull(cb, in viewProjection, 1);
                decimation.Record(cb, in viewProjection);
                GlobalBarrier(cb, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkAccessFlags.ShaderWrite, VkAccessFlags.TransferRead);
                var region = new VkBufferCopy { srcOffset = 0, dstOffset = 0, size = survivorBytes };
                _deviceApi.vkCmdCopyBuffer(cb, decimation.SurvivorBuffer, readback, 1, &region);
                GlobalBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, VkAccessFlags.TransferWrite, VkAccessFlags.HostRead);
                stamp();
            });
            uint* words = (uint*)readbackMapped;
            int count = (int)words[1];
            int[] actual = new int[count];
            for (int i = 0; i < count; i++)
            {
                actual[i] = (int)words[TileDecimationPass.SurvivorHeaderWords + i];
            }

            Array.Sort(actual);
            // The GPU and the CPU round the projected position differently, so an instance exactly on a cell or view border can fall on the other side.
            // The sets must agree up to a handful of border instances (at most 8 plus 0.1 percent of the survivors).
            int difference = actual.Except(expected).Count() + expected.Except(actual).Count();
            worst = Math.Max(worst, difference);
            if (difference > 8 + (expected.Length / 1000))
            {
                bad++;
                Console.WriteLine($"tiledecimate-check trial {trial}: gpu {count} survivors, cpu {expected.Length}, {difference} differ");
            }
        }

        Console.WriteLine($"tiledecimate-check: largest difference over {trials} trials: {worst} instances");
        return bad;
    }

    public const string CombinedCsvHeader = CullCsvHeader + ",candidates,survivors,points_per_cell";

    public static string CombinedCsvLine(string section, long totalNodes, long instances, double buildMs, int edges, CellStats s, CullCounters c, CombinedCounters d)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        return string.Join(',', CullCsvLine(section, totalNodes, instances, buildMs, edges, s, c), d.Candidates.ToString(culture), d.Survivors.ToString(culture),
            d.PointsPerCell.ToString("F2", culture));
    }
}
