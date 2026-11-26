using System.Diagnostics;
using System.Globalization;

namespace Bazaar.GraphRetrieval.Spike;

/// <summary>
/// 24 hour soak (gate "stability"): the vector index (1M x 1024 fp16), a 93M-edge graph and a 100k-node PPR graph stay resident on the GPU while
/// a mixed query loop runs next to the renderer process. Logs one line per minute; checks parity against the CPU every five minutes;
/// injects an allocation failure every ten minutes and checks that the next queries are still correct.
/// </summary>
internal static class SoakBench
{
    public static int Run(string logPath, string stopFile, double hours)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        using SpikeGpu gpu = SpikeGpu.Create();
        var data = new VectorData(1_000_000, 1024);
        var cpuVectors = new CpuVectorEngine(data, Environment.ProcessorCount);
        using var index = new GpuVectorIndex(gpu, data, false);
        var graph = new GraphData(100_000_000);
        var cpuGraph = new CpuGraphEngine(graph, 8);
        using var gpuGraph = new GpuGraphEngine(gpu, graph);
        var pprGraph = new GraphData(1_000_000);
        (uint[] inOff, uint[] inSrc, float[] inCoef) = pprGraph.Transposed();
        using var ppr = new GpuPpr(gpu, pprGraph.Nodes, inOff, inSrc, inCoef);
        long baselineVram = gpu.AllocatedBytes;
        Console.WriteLine($"soak start {DateTime.UtcNow:O}: {data.Rows} vectors, {graph.Edges} edges, {pprGraph.Nodes} ppr nodes, resident {baselineVram / 1048576} MiB");
        File.WriteAllText(logPath, "utc,minutes,iterations,errors,parity_checks,parity_failures,faults_injected,fault_recoveries,vec_p50_ms,vec_p99_ms,khop_p50_ms,khop_p99_ms,ppr_p50_ms,ppr_p99_ms,gpu_alloc_mib,working_set_mib,managed_mib,note\n");

        var vec = new List<double>();
        var khop = new List<double>();
        var pprTimes = new List<double>();
        long iterations = 0, errors = 0, parityChecks = 0, parityFailures = 0, faults = 0, recoveries = 0;
        var clock = Stopwatch.StartNew();
        var minuteMark = TimeSpan.FromMinutes(1);
        var parityMark = TimeSpan.FromMinutes(5);
        var faultMark = TimeSpan.FromMinutes(10);
        string note = string.Empty;
        TimeSpan limit = TimeSpan.FromHours(hours);

        bool Parity()
        {
            parityChecks++;
            bool ok = true;
            float[][] queries = [.. Enumerable.Range((int)(iterations % 100), 4).Select(data.PlantedQuery)];
            TopK[] exact = cpuVectors.Search(queries, 10);
            (TopK[] results, _, _) = index.Search(queries, 10);
            for (int i = 0; i < queries.Length; i++)
            {
                if (TopKTools.Recall(results[i], exact[i]) < 0.9)
                {
                    ok = false;
                }
            }

            uint[] seeds = graph.Seeds((int)iterations, 8);
            LevelDigest[] cpuDigest = cpuGraph.Expand(seeds, 3, 7);
            (_, _, _, LevelDigest[]? gpuDigest) = gpuGraph.Expand([seeds], 3, 7, verify: true);
            if (gpuDigest is null || !gpuDigest.SequenceEqual(cpuDigest.Take(4)))
            {
                ok = false;
            }

            return ok;
        }

        while (!File.Exists(stopFile) && clock.Elapsed < limit)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                (_, _, double vecMs) = index.Search([data.PlantedQuery((int)(iterations % 128))], 10);
                vec.Add(vecMs);
                (_, double khopMs, _, _) = gpuGraph.Expand([graph.Seeds((int)iterations, 8)], 3, 7);
                khop.Add(khopMs);
                if (iterations % 10 == 0)
                {
                    (_, _, double pprMs) = ppr.Run(pprGraph.Seeds((int)iterations, 8), 20, 0.15f);
                    pprTimes.Add(pprMs);
                }

                iterations++;
            }
            catch (Exception ex)
            {
                errors++;
                note = ex.Message.Length > 80 ? ex.Message[..80].Replace(',', ';') : ex.Message.Replace(',', ';');
                Console.WriteLine($"{DateTime.UtcNow:O} ERROR {ex.GetType().Name}: {ex.Message}");
                if (ex.Message.Contains("DeviceLost", StringComparison.OrdinalIgnoreCase) || errors > 1000)
                {
                    File.AppendAllText(logPath, $"{DateTime.UtcNow:O},{clock.Elapsed.TotalMinutes:F1},{iterations},{errors},{parityChecks},{parityFailures},{faults},{recoveries},0,0,0,0,0,0,{gpu.AllocatedBytes / 1048576},{Process.GetCurrentProcess().WorkingSet64 / 1048576},0,FATAL {note}\n");
                    return 4;
                }
            }

            if (clock.Elapsed >= parityMark)
            {
                parityMark += TimeSpan.FromMinutes(5);
                if (!Parity())
                {
                    parityFailures++;
                    note = "parity failure";
                }
            }

            if (clock.Elapsed >= faultMark)
            {
                faultMark += TimeSpan.FromMinutes(10);
                faults++;
                try
                {
                    GpuBuffer huge = gpu.CreateBuffer(40UL * 1024 * 1024 * 1024);
                    gpu.Free(huge);
                    note = "fault allocation unexpectedly succeeded";
                }
                catch (Exception)
                {
                    // Expected: the allocation fails; the next parity check proves the resident indexes still answer correctly.
                    if (Parity())
                    {
                        recoveries++;
                    }
                    else
                    {
                        parityFailures++;
                    }
                }
            }

            if (clock.Elapsed >= minuteMark)
            {
                minuteMark += TimeSpan.FromMinutes(1);
                // A forced collection first, so working set and managed size reflect live data; a steady rise means a real leak.
                GC.Collect();
                long managedMib = GC.GetTotalMemory(false) / 1048576;
                string Pct(List<double> v, double f) => v.Count == 0 ? "NA" : TopKTools.Percentile(v, f).ToString("F3", ci);
                File.AppendAllText(logPath, string.Join(',', DateTime.UtcNow.ToString("O"), clock.Elapsed.TotalMinutes.ToString("F1", ci), iterations, errors, parityChecks, parityFailures, faults, recoveries,
                    Pct(vec, 0.5), Pct(vec, 0.99), Pct(khop, 0.5), Pct(khop, 0.99), Pct(pprTimes, 0.5), Pct(pprTimes, 0.99), gpu.AllocatedBytes / 1048576, Process.GetCurrentProcess().WorkingSet64 / 1048576, managedMib, note) + "\n");
                vec.Clear();
                khop.Clear();
                pprTimes.Clear();
                note = string.Empty;
                _ = baselineVram;
            }

            // About 20 iterations per second: a steady retrieval load next to the renderer, not a saturating one.
            double used = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (used < 50)
            {
                Thread.Sleep((int)(50 - used));
            }
        }

        Console.WriteLine($"soak end {DateTime.UtcNow:O}: {iterations} iterations, {errors} errors, {parityFailures} parity failures of {parityChecks}");
        return parityFailures == 0 && errors == 0 ? 0 : 1;
    }
}
