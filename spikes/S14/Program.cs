using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace S14;

/// <summary>
/// S14 layout-tick benchmark. Prints CSV lines (and writes them to the optional output path).
/// Scenarios per node count N (1k, 5k, 10k), single-threaded and parallel:
///   cold     : random start, ColdTicks ticks, reported for information (not a gate)
///   steady   : warm start from the converged layout, Warmup discarded, then Measured ticks
///   focus    : reverse gravity focus change on the converged layout (pin, rings, warm start)
///   add      : node-addition warm start (1 percent new nodes placed near their neighbours)
/// Tick cost is measured with Stopwatch timestamps; allocations with GC.GetAllocatedBytesForCurrentThread
/// (calling thread only, so parallel runs report the coordinating thread).
/// </summary>
public static class Program
{
    private static readonly int[] Sizes = { 1000, 5000, 10000 };
    private const int ColdTicks = 500;
    private const int Warmup = 30;
    private const int Measured = 300;
    private const ulong GraphSeed = 14;
    private const float FocusRingSpacing = 40f;

#if DEBUG
    private const string BuildConfig = "Debug";
#else
    private const string BuildConfig = "Release";
#endif

    private readonly struct Stats
    {
        public Stats(double median, double p95, double max, long allocMedian, long allocMax)
        {
            Median = median; P95 = p95; Max = max; AllocMedian = allocMedian; AllocMax = allocMax;
        }
        public double Median { get; }
        public double P95 { get; }
        public double Max { get; }
        public long AllocMedian { get; }
        public long AllocMax { get; }
    }

    public static int Main(string[] args)
    {
        string? outPath = args.Length > 0 ? args[0] : null;
        string runId = args.Length > 1 ? args[1] : "0";
        var sb = new StringBuilder();
        void Emit(string line)
        {
            Console.WriteLine(line);
            sb.AppendLine(line);
        }

        Emit("# S14 layout tick benchmark (relative; not the NUC)");
        Emit("# run=" + runId);
        Emit("# utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Emit("# os=" + RuntimeInformation.OSDescription);
        Emit("# arch=" + RuntimeInformation.ProcessArchitecture);
        Emit("# logical_processors=" + Environment.ProcessorCount);
        Emit("# runtime=" + RuntimeInformation.FrameworkDescription);
        Emit("# gc_server=" + GCSettings.IsServerGC + " gc_latency=" + GCSettings.LatencyMode);
        Emit("# build=" + BuildConfig + " graph_seed=" + GraphSeed + " graph=preferential_attachment(m=2) avg_degree~4");
        Emit("# params Kr=" + Layout.Kr + " Kg=" + Layout.Kg + " KRing=" + Layout.KRing + " theta=" + Layout.Theta
             + " focus_ring_spacing=" + FocusRingSpacing + " warmup=" + Warmup + " measured=" + Measured);
        Emit("run,N,mode,scenario,ticks,median_ms,p95_ms,max_ms,alloc_median_B,alloc_max_B,mean_disp_end,ring_err_start,ring_err_end,tree_grows,tree_cells");

        foreach (int n in Sizes)
        {
            int delta = n / 100;
            var full = Graph.PreferentialAttachment(n + delta, 2, GraphSeed);
            var baseGraph = full.Prefix(n);

            // Cold convergence, single-threaded. Its converged state is the warm start for every
            // steady, focus and add measurement at this N.
            var cold = new Layout(baseGraph, parallel: false);
            cold.InitRandom(new Rng(1000UL + (ulong)n), 500f);
            var coldStats = Measure(cold, 0, ColdTicks);
            Emit(Line(runId, n, "st", "cold", ColdTicks, coldStats, cold.LastMeanDisplacement, double.NaN, double.NaN, cold.GrowCount, cold.TreeCells));
            var baseState = cold.Save();
            if (baseState.Pos.Any(float.IsNaN)) Emit("# WARNING NaN in converged state at N=" + n);

            // Determinism check: parallel and single-threaded must agree exactly (per-node force order is fixed).
            var a = new Layout(baseGraph, parallel: false);
            var b = new Layout(baseGraph, parallel: true);
            a.Load(baseState);
            b.Load(baseState);
            for (int t = 0; t < 5; t++) { a.Tick(); b.Tick(); }
            float maxDiff = 0f;
            for (int i = 0; i < 2 * n; i++) maxDiff = MathF.Max(maxDiff, MathF.Abs(a.Pos[i] - b.Pos[i]));
            Emit("# check N=" + n + " par_vs_st_max_abs_pos_diff_after_5_ticks=" + maxDiff.ToString("G9", CultureInfo.InvariantCulture));

            // Accuracy check: Barnes-Hut repulsion against brute force on about 20 sample nodes at the converged state.
            var checkTree = new QuadTree(n);
            checkTree.Build(baseState.Pos, cold.Mass, n);
            var checkStack = new int[4 * QuadTree.MaxDepth + 16];
            double worstRel = 0;
            for (int s = 0; s < n; s += Math.Max(1, n / 20))
            {
                float xi = baseState.Pos[2 * s], yi = baseState.Pos[2 * s + 1], mi = cold.Mass[s];
                float bhx = 0f, bhy = 0f;
                checkTree.Repel(s, xi, yi, mi, Layout.Kr, Layout.Theta, checkStack, ref bhx, ref bhy);
                double bx = 0, by = 0;
                for (int j = 0; j < n; j++)
                {
                    if (j == s) continue;
                    double dx = xi - baseState.Pos[2 * j], dy = yi - baseState.Pos[2 * j + 1];
                    double d2 = dx * dx + dy * dy + 0.01;
                    double sc = Layout.Kr * mi * cold.Mass[j] / d2;
                    bx += sc * dx;
                    by += sc * dy;
                }
                double norm = Math.Sqrt(bx * bx + by * by);
                double err = norm > 0 ? Math.Sqrt((bhx - bx) * (bhx - bx) + (bhy - by) * (bhy - by)) / norm : 0;
                worstRel = Math.Max(worstRel, err);
            }
            Emit("# check N=" + n + " barnes_hut_vs_bruteforce_worst_rel_err_theta=" + Layout.Theta.ToString("G3", CultureInfo.InvariantCulture)
                 + " value=" + worstRel.ToString("G4", CultureInfo.InvariantCulture));

            foreach (bool par in new[] { false, true })
            {
                string mode = par ? "par" : "st";

                var steady = new Layout(baseGraph, par);
                steady.Load(baseState);
                var sSteady = Measure(steady, Warmup, Measured);
                Emit(Line(runId, n, mode, "steady", Measured, sSteady, steady.LastMeanDisplacement, double.NaN, double.NaN, steady.GrowCount, steady.TreeCells));

                var focus = new Layout(baseGraph, par);
                focus.Load(baseState);
                int focusNode = new Rng(7000UL + (ulong)n).NextInt(n);
                focus.SetFocus(focusNode, FocusRingSpacing);
                double ringStart = focus.RingError();
                var sFocus = Measure(focus, 0, Measured);
                Emit(Line(runId, n, mode, "focus_reverse_gravity", Measured, sFocus, focus.LastMeanDisplacement, ringStart, focus.RingError(), focus.GrowCount, focus.TreeCells));

                var add = new Layout(full, par);
                Array.Copy(baseState.Pos, add.Pos, 2 * n);
                Array.Copy(baseState.PrevForce, add.PrevForce, 2 * n);
                add.Speed = baseState.Speed;
                PlaceNewNodes(add, full, n, new Rng(8000UL + (ulong)n));
                var sAdd = Measure(add, 0, Measured);
                Emit(Line(runId, n, mode, "add_" + delta + "_nodes", Measured, sAdd, add.LastMeanDisplacement, double.NaN, double.NaN, add.GrowCount, add.TreeCells));
            }
        }

        if (outPath is not null) File.WriteAllText(outPath, sb.ToString());
        return 0;
    }

    /// <summary>Places nodes [first, count) near the mean of their already-placed neighbours, with small jitter.</summary>
    private static void PlaceNewNodes(Layout l, Graph g, int first, Rng rng)
    {
        for (int v = first; v < g.NodeCount; v++)
        {
            float sx = 0f, sy = 0f;
            int got = 0;
            for (int k = g.AdjStart[v]; k < g.AdjStart[v + 1]; k++)
            {
                int j = g.Adj[k];
                if (j >= v) continue;
                sx += l.Pos[2 * j];
                sy += l.Pos[2 * j + 1];
                got++;
            }
            if (got > 0) { sx /= got; sy /= got; }
            l.Pos[2 * v] = sx + (rng.NextFloat() * 2f - 1f) * 2f;
            l.Pos[2 * v + 1] = sy + (rng.NextFloat() * 2f - 1f) * 2f;
        }
    }

    private static Stats Measure(Layout l, int warm, int count)
    {
        for (int w = 0; w < warm; w++) l.Tick();
        var ms = new double[count];
        var alloc = new long[count];
        long freq = Stopwatch.Frequency;
        for (int t = 0; t < count; t++)
        {
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            l.Tick();
            long t1 = Stopwatch.GetTimestamp();
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            ms[t] = (t1 - t0) * 1000.0 / freq;
            alloc[t] = a1 - a0;
        }
        var sorted = (double[])ms.Clone();
        Array.Sort(sorted);
        var sortedAlloc = (long[])alloc.Clone();
        Array.Sort(sortedAlloc);
        int p95Index = Math.Min(count - 1, (int)Math.Ceiling(0.95 * count) - 1);
        return new Stats(
            median: sorted[count / 2],
            p95: sorted[p95Index],
            max: sorted[count - 1],
            allocMedian: sortedAlloc[count / 2],
            allocMax: sortedAlloc[count - 1]);
    }

    private static string Line(string run, int n, string mode, string scenario, int ticks, Stats s,
        double meanDisp, double ringStart, double ringEnd, int grows, int cells)
    {
        var ci = CultureInfo.InvariantCulture;
        return string.Join(",",
            run, n.ToString(ci), mode, scenario, ticks.ToString(ci),
            s.Median.ToString("F4", ci), s.P95.ToString("F4", ci), s.Max.ToString("F4", ci),
            s.AllocMedian.ToString(ci), s.AllocMax.ToString(ci),
            meanDisp.ToString("G6", ci),
            double.IsNaN(ringStart) ? "" : ringStart.ToString("F2", ci),
            double.IsNaN(ringEnd) ? "" : ringEnd.ToString("F2", ci),
            grows.ToString(ci), cells.ToString(ci));
    }
}
