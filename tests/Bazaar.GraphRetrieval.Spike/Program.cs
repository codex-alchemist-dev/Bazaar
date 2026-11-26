using System.Diagnostics;
using System.Globalization;
using Bazaar.GraphRetrieval.Spike;

// Spike S28 harness (docs/design/64-gpu-graph-retrieval.md section 13). Modes: vec, graph, pg, soak.
string mode = args.Length > 0 ? args[0] : "vec";
string outDir = Environment.GetEnvironmentVariable("S28_OUT") ?? Path.Combine(AppContext.BaseDirectory, "results");
Directory.CreateDirectory(outDir);
bool quick = args.Contains("quick");
CultureInfo ci = CultureInfo.InvariantCulture;

switch (mode)
{
    case "soak":
        return SoakBench.Run(Path.Combine(outDir, "s28-soak.csv"), Environment.GetEnvironmentVariable("S28_STOP") ?? Path.Combine(outDir, "soak.stop"), args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, ci, out double soakHours) ? soakHours : 24);
    case "pg":
        return PgBench.Run(Path.Combine(outDir, "s28-pgvector.csv"), quick);
    case "graph":
        return GraphBench.Run(Path.Combine(outDir, "s28-graph.csv"), quick);
    case "vec":
        return VectorBench.Run(Path.Combine(outDir, "s28-vector.csv"), quick);
    default:
        Console.WriteLine($"unknown mode {mode}");
        return 2;
}

internal static class VectorBench
{
    public const string Header = "arm,dim,n,k,batch,reps,p50_ms,p99_ms,qps,recall_mean,recall_min,gpu_ms_p50,vram_mib,note";

    public static int Run(string csvPath, bool quick)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        if (Environment.GetEnvironmentVariable("BAZAAR_BENCH_BOOST") == "1")
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        }

        var csv = new List<string> { Header };
        // S28_RESUME=1: keep the rows already written and skip every (dim, n) configuration whose last arm (gpu-int8) is complete.
        var done = new HashSet<(int, long)>();
        if (Environment.GetEnvironmentVariable("S28_RESUME") == "1" && File.Exists(csvPath))
        {
            foreach (string line in File.ReadAllLines(csvPath).Skip(1))
            {
                string[] f = line.Split(',');
                csv.Add(line);
                if (f[0] == "gpu-int8" && f[3] == "10" && f[4] == "128" || (f[0].StartsWith("gpu-int8", StringComparison.Ordinal) && f[13].StartsWith("not resident", StringComparison.Ordinal)))
                {
                    done.Add((int.Parse(f[1], ci), long.Parse(f[2], ci)));
                }
            }
        }

        void Emit(string arm, int dim, long n, int k, int batch, IReadOnlyList<double> wall, IReadOnlyList<double> gpu, IReadOnlyList<double> recall, long vram, string note)
        {
            double p50 = TopKTools.Percentile(wall, 0.5);
            string line = string.Join(',', arm, dim, n, k, batch, wall.Count, p50.ToString("F3", ci), TopKTools.Percentile(wall, 0.99).ToString("F3", ci),
                (batch * 1000.0 / p50).ToString("F1", ci), recall.Count == 0 ? "NA" : recall.Average().ToString("F4", ci), recall.Count == 0 ? "NA" : recall.Min().ToString("F4", ci),
                gpu.Count == 0 ? "NA" : TopKTools.Percentile(gpu, 0.5).ToString("F3", ci), (vram / 1048576.0).ToString("F0", ci), note);
            csv.Add(line);
            File.WriteAllLines(csvPath, csv);
            Console.WriteLine(line);
        }

        using SpikeGpu gpu = SpikeGpu.Create();
        Console.WriteLine($"gpu {gpu.DeviceName} device-local {gpu.DeviceLocalBytes / 1073741824.0:F2} GiB; cores {Environment.ProcessorCount}");
        int[] dims = quick ? [1024] : [768, 1024, 1536];
        long[] sizes = quick ? [10_000, 100_000] : [10_000, 100_000, 1_000_000, 5_000_000, 10_000_000];
        (int K, int B)[] grid = [(10, 1), (20, 1), (50, 1), (10, 8), (10, 32), (50, 32), (10, 128)];
        int[] threadCounts = Environment.GetEnvironmentVariable("S28_GPUONLY") == "1" ? [] : [1, 4, 8, Environment.ProcessorCount];
        const int PoolSize = 128;
        const double SeriesBudgetSeconds = 8;

        foreach (int dim in dims)
        {
            foreach (long n in sizes)
            {
                if (done.Contains((dim, n)))
                {
                    continue;
                }

                if ((double)n * dim > 1.1e10)
                {
                    Emit("host", dim, n, 0, 0, [double.NaN], [], [], 0, "skipped: fp32 host copy would exceed the memory budget");
                    continue;
                }

                var watch = Stopwatch.StartNew();
                var data = new VectorData(n, dim);
                float[][] pool = [.. Enumerable.Range(0, PoolSize).Select(data.PlantedQuery)];
                Console.WriteLine($"-- dim {dim} n {n}: data {watch.Elapsed.TotalSeconds:F1}s");
                var cpuAll = new CpuVectorEngine(data, Environment.ProcessorCount);
                TopK[] exact = cpuAll.Search(pool, 50);

                TopK Prefix(TopK full, int k) => new(full.Rows[..k], full.Scores[..k]);

                void Series(string arm, int k, int batch, Func<float[][], int, (TopK[] Results, double GpuMs, double WallMs)> search, long vram, string note)
                {
                    int reps = quick ? Math.Min(batch == 1 ? 20 : 5, 20) : (batch == 1 ? 100 : batch == 8 ? 50 : batch == 32 ? 25 : 10);
                    var wall = new List<double>();
                    var gpuTimes = new List<double>();
                    var recall = new List<double>();
                    var clock = Stopwatch.StartNew();
                    for (int rep = -2; rep < reps; rep++)
                    {
                        int start = (rep + 2) * batch % PoolSize;
                        float[][] queries = [.. Enumerable.Range(0, batch).Select(i => pool[(start + i) % PoolSize])];
                        (TopK[] results, double gpuMs, double wallMs) = search(queries, k);
                        if (rep >= 0)
                        {
                            wall.Add(wallMs);
                            if (gpuMs > 0)
                            {
                                gpuTimes.Add(gpuMs);
                            }

                            for (int i = 0; i < batch; i++)
                            {
                                recall.Add(TopKTools.Recall(results[i], Prefix(exact[(start + i) % PoolSize], k)));
                            }
                        }

                        if (clock.Elapsed.TotalSeconds > SeriesBudgetSeconds && wall.Count >= 1 && (wall.Count >= 3 || wallMs > 20000))
                        {
                            break;
                        }
                    }

                    Emit(arm, dim, n, k, batch, wall, gpuTimes, recall, vram, note);
                }

                foreach (int threads in threadCounts)
                {
                    // Time-box: 1 and 4 threads only up to 1M rows, 8 threads up to 5M rows; all cores at every size.
                    if ((threads is 1 or 4 && n > 1_000_000) || (threads == 8 && n > 5_000_000))
                    {
                        Emit($"cpu{threads}", dim, n, 0, 0, [double.NaN], [], [], 0, "not run: time-box (CPU arms at 1 and 4 threads stop at 1M rows, 8 threads at 5M rows)");
                        continue;
                    }

                    var cpu = new CpuVectorEngine(data, threads);
                    double estimate = double.NaN;
                    foreach ((int k, int b) in grid)
                    {
                        if (!double.IsNaN(estimate) && estimate * b > 90_000)
                        {
                            Emit($"cpu{threads}", dim, n, k, b, [double.NaN], [], [], 0, "skipped: estimated over 90 s per batch");
                            continue;
                        }

                        Series($"cpu{threads}", k, b, (queries, kk) =>
                        {
                            long t0 = Stopwatch.GetTimestamp();
                            TopK[] r = cpu.Search(queries, kk);
                            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                            return (r, 0, ms);
                        }, 0, "float32 values of the stored fp16 rows");
                        if (b == 1 && double.IsNaN(estimate))
                        {
                            estimate = double.Parse(csv[^1].Split(',')[6], ci);
                        }
                    }
                }

                foreach (bool int8 in new[] { false, true })
                {
                    string arm = int8 ? "gpu-int8" : "gpu-fp16";
                    try
                    {
                        using var index = new GpuVectorIndex(gpu, data, int8);
                        long vram = index.GpuBytes;
                        foreach ((int k, int b) in grid)
                        {
                            Series(arm, k, b, (queries, kk) => index.Search(queries, kk), vram, int8 ? "int8 first pass, fp16 rescore of 4k+16 on the host" : "fp16 matrix, k+16 candidates per chunk");
                        }

                        if (index.Overflows > 0)
                        {
                            Console.WriteLine($"WARNING {arm}: {index.Overflows} candidate lists overflowed");
                        }
                    }
                    catch (OutOfMemoryException ex)
                    {
                        Emit(arm, dim, n, 0, 0, [double.NaN], [], [], 0, $"not resident: {ex.Message}");
                    }
                }
            }
        }

        return 0;
    }
}
