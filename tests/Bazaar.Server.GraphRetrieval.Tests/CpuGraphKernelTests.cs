namespace Bazaar.Server.GraphRetrieval.Tests;

public sealed class CpuGraphKernelTests : GraphKernelConformanceTests
{
    protected override IGraphKernel CreateKernel(int threads) =>
        new CpuGraphKernel(new CpuGraphKernelOptions { MaxDegreeOfParallelism = threads, ChunkSize = 64 });

    [Fact]
    public void Default_options_are_valid_and_follow_doc_64()
    {
        var o = new CpuGraphKernelOptions();
        o.Validate();
        Assert.Equal(0.15, o.Alpha);
        Assert.Equal(20, o.Iterations);
        Assert.Equal(8, o.ClosureMaxDepth);
        Assert.Equal(0.2, o.ClosureShare);
        Assert.Equal(2_000, o.MaxEdgesPerNode);
        Assert.True(o.MaxDegreeOfParallelism >= 1);
    }

    [Theory]
    [InlineData(0, 0.5, 0.15, 20)]
    [InlineData(1, 0.0, 0.15, 20)]
    [InlineData(1, 0.5, 1.0, 20)]
    [InlineData(1, 0.5, 0.15, 0)]
    public void Invalid_options_are_rejected(int threads, double decay, double alpha, int iterations)
    {
        var o = new CpuGraphKernelOptions { MaxDegreeOfParallelism = threads, HopDecay = decay, Alpha = alpha, Iterations = iterations };
        Assert.Throws<ArgumentException>(() => new CpuGraphKernel(o));
    }

    [Fact]
    public async Task Chunk_size_does_not_change_results()
    {
        GraphSnapshot snap = TestGraph.Random(3000, 5, 17);
        var req = new RankRequest { Snapshot = snap, Seeds = [new SeedScore(1, 1)], Direction = EdgeDirection.Both };
        NodeScores a = (await new CpuGraphKernel(new CpuGraphKernelOptions { ChunkSize = 16, MaxDegreeOfParallelism = 4 }).TryRankAsync(req, TestContext.Current.CancellationToken)).Value!;
        NodeScores b = (await new CpuGraphKernel(new CpuGraphKernelOptions { ChunkSize = 5000, MaxDegreeOfParallelism = 1 }).TryRankAsync(req, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(a.Scores, b.Scores);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_work()
    {
        GraphSnapshot snap = TestGraph.Random(2000, 5, 3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var req = new ExpansionRequest { Snapshot = snap, Seeds = [new SeedScore(1, 1)], MaxHops = 3 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new CpuGraphKernel().TryExpandAsync(req, cts.Token));
    }

    [Fact]
    public async Task Request_values_override_the_option_defaults()
    {
        var g = new TestGraph().Edge("A", "B");
        var kernel = new CpuGraphKernel(new CpuGraphKernelOptions { HopDecay = 0.9 });
        ExpansionResult r = (await kernel.TryExpandAsync(new ExpansionRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], MaxHops = 1 }, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(0.9, r.Nodes.Single(n => n.Node == g.N("B")).Score, 1e-9);
        ExpansionResult o = (await kernel.TryExpandAsync(new ExpansionRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], MaxHops = 1, HopDecay = 0.1 }, TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(0.1, o.Nodes.Single(n => n.Node == g.N("B")).Score, 1e-9);
    }
}
