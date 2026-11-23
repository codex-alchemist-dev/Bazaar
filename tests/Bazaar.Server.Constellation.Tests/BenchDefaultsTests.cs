using Bazaar.Constellation.Bench;

namespace Bazaar.Server.Tests.Constellation;

public sealed class BenchDefaultsTests
{
    [Fact]
    public void Standard_run_covers_the_5k_and_10k_graphs_only()
    {
        Assert.Equal([5_000, 10_000], BenchNodeCounts.Resolve([]));
    }

    [Fact]
    public void Large_flag_adds_100k_and_1M_after_the_standard_counts()
    {
        Assert.Equal([5_000, 10_000, 100_000, 1_000_000], BenchNodeCounts.Resolve(["--large"]));
    }

    [Fact]
    public void Explicit_list_replaces_the_defaults()
    {
        Assert.Equal([2_000, 3_000], BenchNodeCounts.Resolve(["2000,3000"]));
    }

    [Fact]
    public void Flag_order_does_not_change_the_explicit_list()
    {
        Assert.Equal([1_000], BenchNodeCounts.Resolve(["--large", "1000"]));
    }
}
