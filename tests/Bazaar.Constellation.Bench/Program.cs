using System.Diagnostics;
using Bazaar.Constellation.Bench;
using Bazaar.Constellation.Rendering;

// CPU-side Constellation benchmark. Not a PR gate (doc 19: nightly, on the owner's hardware).
// GPU frame time, instancing and tile streaming are measured by the Vulkan backend on the PC and phone, not here.
// Usage: dotnet run -c Release -- [--large] [nodeCounts comma-separated, default 5000,10000; --large adds 100000,1000000]

int[] counts = BenchNodeCounts.Resolve(args);

const int Repeats = 5;
const float CellSize = 16f;
const int Queries = 1_000;
const float QueryRadius = 8f;

Console.WriteLine("nodes,operation,median_ms,proposed_budget_ms,within_budget");
foreach (int nodes in counts)
{
    var random = new Random(14);
    var positions = new float[nodes * 2];
    float extent = MathF.Sqrt(nodes) * 4f;
    for (int i = 0; i < positions.Length; i++)
    {
        positions[i] = (float)((random.NextDouble() * 2.0 * extent) - extent);
    }

    var target = new float[nodes * 2];
    for (int i = 0; i < target.Length; i++)
    {
        target[i] = positions[i] + 1f;
    }

    var from = new RenderFrame(1, 1, 2, positions);
    var to = new RenderFrame(1, 2, 2, target);

    Report(nodes, "interpolate_sample", Median(() =>
    {
        var interpolator = new FrameInterpolator(1.0);
        interpolator.Push(from, 0);
        interpolator.Push(to, 0);
        var output = new float[target.Length];
        interpolator.Sample(0.5, output);
    }), 2.0);

    var grid = new SpatialGrid(CellSize);
    Report(nodes, "grid_build", Median(() => grid.Build(positions, 2)), 8.0);

    grid.Build(positions, 2);
    Report(nodes, "grid_query_x1000", Median(() =>
    {
        for (int q = 0; q < Queries; q++)
        {
            float x = (float)((random.NextDouble() * 2.0 * extent) - extent);
            float y = (float)((random.NextDouble() * 2.0 * extent) - extent);
            grid.Nearest(x, y, QueryRadius);
        }
    }), 1.0);
}

static double Median(Action action)
{
    action(); // warm-up
    var samples = new double[Repeats];
    for (int i = 0; i < Repeats; i++)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    Array.Sort(samples);
    return samples[Repeats / 2];
}

static void Report(int nodes, string operation, double medianMs, double budgetMs) =>
    Console.WriteLine(FormattableString.Invariant($"{nodes},{operation},{medianMs:F3},{budgetMs:F1},{(medianMs <= budgetMs ? "yes" : "no")}"));
