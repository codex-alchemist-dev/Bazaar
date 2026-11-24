using System.Diagnostics;

namespace Bazaar.Server.GraphRetrieval.Tests;

/// <summary>
/// Benchmark harness (doc 64 section 13). Skipped unless <c>BAZAAR_GRAPH_BENCH=1</c>; it measures expansion plus PPR on a
/// 1M-edge synthetic graph at 1 thread and at all cores, and prints the numbers. It asserts only that results agree.
/// </summary>
public sealed class Benchmark(Xunit.ITestOutputHelper output)
{
    [Fact]
    public async Task Expansion_and_ppr_on_a_million_edges()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BAZAAR_GRAPH_BENCH") == "1", "Set BAZAAR_GRAPH_BENCH=1 to run the graph kernel benchmark.");

        GraphSnapshot g = TestGraph.Random(100_000, 10, 99);
        output.WriteLine($"graph: {g.NodeCount} nodes, {g.EdgeCount} edges");
        SeedScore[] seeds = [.. Enumerable.Range(0, 32).Select(i => new SeedScore(i * 3001, 1.0 - (i * 0.01)))];

        NodeScores? reference = null;
        foreach (int threads in new[] { 1, Environment.ProcessorCount })
        {
            var kernel = new CpuGraphKernel(new CpuGraphKernelOptions { MaxDegreeOfParallelism = threads });
            var sw = Stopwatch.StartNew();
            ExpansionResult exp = (await kernel.TryExpandAsync(new ExpansionRequest { Snapshot = g, Seeds = seeds, MaxHops = 3, Direction = EdgeDirection.Both }, TestContext.Current.CancellationToken)).Value!;
            long expandMs = sw.ElapsedMilliseconds;
            sw.Restart();
            NodeScores ppr = (await kernel.TryRankAsync(new RankRequest { Snapshot = g, Seeds = seeds, Direction = EdgeDirection.Both, Limit = 100 }, TestContext.Current.CancellationToken)).Value!;
            long pprMs = sw.ElapsedMilliseconds;
            output.WriteLine($"threads={threads}: expand {expandMs} ms ({exp.Nodes.Count} reached), ppr {pprMs} ms ({ppr.Iterations} iterations)");
            if (reference is null)
            {
                reference = ppr;
            }
            else
            {
                Assert.Equal(reference.Scores, ppr.Scores);
            }
        }
    }
}
