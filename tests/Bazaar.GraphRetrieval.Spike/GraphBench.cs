using System.Diagnostics;
using System.Globalization;

namespace Bazaar.GraphRetrieval.Spike;

internal static class GraphBench
{
    public const string Header = "task,arm,edges,nodes,seeds,depth,batch,reps,p50_ms,p99_ms,qps,reached_p50,verified,gpu_mib,note";

    public static int Run(string csvPath, bool quick)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (Environment.GetEnvironmentVariable("BAZAAR_BENCH_BOOST") == "1")
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }

        var csv = new List<string> { Header };
        void Emit(string task, string arm, GraphData g, int seeds, int depth, int batch, IReadOnlyList<double> wall, IReadOnlyList<double> reached, string verified, long gpuBytes, string note)
        {
            double p50 = TopKTools.Percentile(wall, 0.5);
            string line = string.Join(',', task, arm, g.Edges, g.Nodes, seeds, depth, batch, wall.Count, p50.ToString("F3", ci), TopKTools.Percentile(wall, 0.99).ToString("F3", ci),
                (batch * 1000.0 / p50).ToString("F1", ci), reached.Count == 0 ? "NA" : TopKTools.Percentile(reached, 0.5).ToString("F0", ci), verified, (gpuBytes / 1048576.0).ToString("F0", ci), note);
            csv.Add(line);
            File.WriteAllLines(csvPath, csv);
            Console.WriteLine(line);
        }

        using SpikeGpu gpu = SpikeGpu.Create();
        Console.WriteLine($"gpu {gpu.DeviceName}; cores {Environment.ProcessorCount}");
        long[] edgeSizes = quick ? [1_000_000, 10_000_000] : [1_000_000, 10_000_000, 100_000_000, 500_000_000, 1_000_000_000];
        int[] threadCounts = [1, 8, Environment.ProcessorCount];
        const double Budget = 8;
        const uint AllKinds = 7;
        const uint DagKinds = 6;   // derived_from and supersedes

        foreach (long edgeTarget in edgeSizes)
        {
            var watch = Stopwatch.StartNew();
            var g = new GraphData(edgeTarget);
            Console.WriteLine($"-- graph {g.Edges} edges {g.Nodes} nodes, generated in {watch.Elapsed.TotalSeconds:F1}s");
            var cpus = threadCounts.ToDictionary(t => t, t => new CpuGraphEngine(g, t));
            GpuGraphEngine? gpuGraph = null;
            try
            {
                gpuGraph = new GpuGraphEngine(gpu, g);
            }
            catch (OutOfMemoryException ex)
            {
                Emit("resident", "gpu", g, 0, 0, 0, [double.NaN], [], "NA", 0, $"not resident: {ex.Message}");
            }

            // (task, seeds, depth, kinds)
            var cases = new List<(string Task, int Seeds, int Depth, uint Kinds)>();
            foreach (int seeds in new[] { 1, 8, 32 })
            {
                foreach (int hops in new[] { 1, 2, 3 })
                {
                    cases.Add(("khop", seeds, hops, AllKinds));
                }

                foreach (int depth in new[] { 2, 8, 32 })
                {
                    cases.Add(("dag", seeds, depth, DagKinds));
                }
            }

            foreach ((string task, int seeds, int depth, uint kinds) in cases)
            {
                // Verification: one query, CPU digest against GPU digest, per level.
                string verified = "NA";
                if (gpuGraph is not null)
                {
                    uint[] seedSet = g.Seeds(9999, seeds);
                    LevelDigest[] cpuDigest = cpus[Environment.ProcessorCount].Expand(seedSet, depth, kinds);
                    (_, _, _, LevelDigest[]? gpuDigest) = gpuGraph.Expand([seedSet], depth, kinds, verify: true);
                    verified = gpuDigest is not null && gpuDigest.Select(d => d).SequenceEqual(cpuDigest.Take(depth + 1)) ? "true" : "FALSE";
                }

                foreach (int batch in new[] { 1, 32 })
                {
                    int reps = quick ? 5 : batch == 1 ? 60 : 12;
                    foreach (int threads in threadCounts)
                    {
                        var wall = new List<double>();
                        var reached = new List<double>();
                        var clock = Stopwatch.StartNew();
                        for (int rep = -1; rep < reps; rep++)
                        {
                            uint[][] sets = [.. Enumerable.Range(0, batch).Select(i => g.Seeds(((rep + 1) * 1000) + i, seeds))];
                            long t0 = Stopwatch.GetTimestamp();
                            LevelDigest[]? last = null;
                            foreach (uint[] set in sets)
                            {
                                last = cpus[threads].Expand(set, depth, kinds);
                            }

                            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                            if (rep >= 0)
                            {
                                wall.Add(ms);
                                reached.Add(last!.Sum(d => (double)d.Count));
                            }

                            if (clock.Elapsed.TotalSeconds > Budget && wall.Count >= 3)
                            {
                                break;
                            }
                        }

                        Emit(task, $"cpu{threads}", g, seeds, depth, batch, wall, reached, verified, 0, "queries run back to back");
                    }

                    if (gpuGraph is not null)
                    {
                        var wall = new List<double>();
                        var reached = new List<double>();
                        var clock = Stopwatch.StartNew();
                        for (int rep = -1; rep < reps; rep++)
                        {
                            uint[][] sets = [.. Enumerable.Range(0, batch).Select(i => g.Seeds(((rep + 1) * 1000) + i, seeds))];
                            (double gpuMs, double wallMs, uint[][] ends, _) = gpuGraph.Expand(sets, depth, kinds);
                            _ = gpuMs;
                            if (rep >= 0)
                            {
                                wall.Add(wallMs);
                                reached.Add(ends[^1][depth]);
                            }

                            if (clock.Elapsed.TotalSeconds > Budget && wall.Count >= 3)
                            {
                                break;
                            }
                        }

                        Emit(task, "gpu", g, seeds, depth, batch, wall, reached, verified, gpuGraph.GpuBytes,
                            gpuGraph.Overflowed ? "reached list overflowed 4M" : "queries run back to back in one submission; reached list stays on the device");
                    }
                }
            }

            gpuGraph?.Dispose();
        }

        // Personalised PageRank over subgraphs of 1k to 1M nodes (mean out-degree 10, same generator).
        foreach (int nodes in quick ? new[] { 1_000, 10_000 } : new[] { 1_000, 10_000, 100_000, 1_000_000 })
        {
            var g = new GraphData((long)nodes * 10);
            (uint[] inOff, uint[] inSrc, float[] inCoef) = g.Transposed();
            Console.WriteLine($"-- ppr graph {g.Edges} edges {g.Nodes} nodes");
            using var gpuPpr = new GpuPpr(gpu, g.Nodes, inOff, inSrc, inCoef);
            const int Iterations = 20;
            const float Alpha = 0.15f;
            const int Seeds = 8;
            var reference = new CpuPpr(g, inOff, inSrc, inCoef, Environment.ProcessorCount).Run(g.Seeds(4242, Seeds), Iterations, Alpha);
            (float[] gpuScores, _, _) = gpuPpr.Run(g.Seeds(4242, Seeds), Iterations, Alpha);
            double maxDiff = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                maxDiff = Math.Max(maxDiff, Math.Abs(reference[i] - gpuScores[i]));
            }

            string verified = maxDiff <= 1e-5 ? $"true (max abs diff {maxDiff:E1})" : $"FALSE (max abs diff {maxDiff:E1})";
            foreach (int threads in threadCounts)
            {
                var cpu = new CpuPpr(g, inOff, inSrc, inCoef, threads);
                var wall = new List<double>();
                var clock = Stopwatch.StartNew();
                for (int rep = -1; rep < (quick ? 5 : 30); rep++)
                {
                    uint[] seeds = g.Seeds(rep + 10, Seeds);
                    long t0 = Stopwatch.GetTimestamp();
                    float[] scores = cpu.Run(seeds, Iterations, Alpha);
                    _ = TopOf(scores, 50);
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (rep >= 0)
                    {
                        wall.Add(ms);
                    }

                    if (clock.Elapsed.TotalSeconds > Budget && wall.Count >= 3)
                    {
                        break;
                    }
                }

                Emit("ppr", $"cpu{threads}", g, Seeds, Iterations, 1, wall, [], verified, 0, "20 iterations, alpha 0.15, includes top-50 selection");
            }

            var gpuWall = new List<double>();
            var gpuClock = Stopwatch.StartNew();
            for (int rep = -1; rep < (quick ? 5 : 30); rep++)
            {
                uint[] seeds = g.Seeds(rep + 10, Seeds);
                (float[] scores, _, double wallMs) = gpuPpr.Run(seeds, Iterations, Alpha);
                long t0 = Stopwatch.GetTimestamp();
                _ = TopOf(scores, 50);
                wallMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (rep >= 0)
                {
                    gpuWall.Add(wallMs);
                }

                if (gpuClock.Elapsed.TotalSeconds > Budget && gpuWall.Count >= 3)
                {
                    break;
                }
            }

            Emit("ppr", "gpu", g, Seeds, Iterations, 1, gpuWall, [], verified, gpu.AllocatedBytes, "20 iterations, alpha 0.15, scores read back, includes top-50 selection");
        }

        return 0;
    }

    private static int[] TopOf(float[] scores, int k)
    {
        var heap = new PriorityQueue<int, float>();
        for (int i = 0; i < scores.Length; i++)
        {
            if (heap.Count < k)
            {
                heap.Enqueue(i, scores[i]);
            }
            else if (heap.TryPeek(out _, out float worst) && scores[i] > worst)
            {
                heap.DequeueEnqueue(i, scores[i]);
            }
        }

        var result = new List<int>();
        while (heap.TryDequeue(out int node, out _))
        {
            result.Add(node);
        }

        return [.. result];
    }
}
