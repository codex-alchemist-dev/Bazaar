using Bazaar.Server.VmBench;
using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench.Tests;

public sealed class VmBenchComparisonTests
{
    private static VmBenchStat Stat(VmBenchScenario scenario, double median, BenchGuestOs os = BenchGuestOs.Linux, int count = 5) =>
        new(scenario, os, "x", count, 0, median, median, median, median, median);

    private static VmBenchReport Report(params VmBenchStat[] stats) => new(
        "r", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "host", new VmBenchOptions(), [], stats);

    [Fact]
    public void Flags_a_twenty_percent_slower_boot()
    {
        var baseline = Report(Stat(VmBenchScenario.BootToReady, 10));
        var current = Report(Stat(VmBenchScenario.BootToReady, 12));

        var regressions = VmBenchComparison.Compare(baseline, current);

        var r = Assert.Single(regressions);
        Assert.Equal(VmBenchScenario.BootToReady, r.Scenario);
        Assert.Equal(BenchGuestOs.Linux, r.Os);
        Assert.Equal(10, r.BaselineMedian);
        Assert.Equal(12, r.CurrentMedian);
        Assert.Equal(1.2, r.RatioChange, 6);
    }

    [Fact]
    public void Flags_a_twenty_percent_lower_share_throughput()
    {
        var baseline = Report(Stat(VmBenchScenario.ShareThroughput, 100));
        var current = Report(Stat(VmBenchScenario.ShareThroughput, 80));

        var r = Assert.Single(VmBenchComparison.Compare(baseline, current));

        Assert.Equal(VmBenchScenario.ShareThroughput, r.Scenario);
        Assert.Equal(0.8, r.RatioChange, 6);
    }

    [Fact]
    public void Flags_higher_ram_as_a_regression()
    {
        var baseline = Report(Stat(VmBenchScenario.LoadedRam, 1000));
        var current = Report(Stat(VmBenchScenario.LoadedRam, 1300));

        Assert.Single(VmBenchComparison.Compare(baseline, current));
    }

    [Fact]
    public void Ignores_changes_within_tolerance_and_improvements()
    {
        var baseline = Report(
            Stat(VmBenchScenario.BootToReady, 10),
            Stat(VmBenchScenario.ShareThroughput, 100),
            Stat(VmBenchScenario.Save, 10));
        var current = Report(
            Stat(VmBenchScenario.BootToReady, 10.5),
            Stat(VmBenchScenario.ShareThroughput, 95),
            Stat(VmBenchScenario.Save, 5));

        Assert.Empty(VmBenchComparison.Compare(baseline, current));
    }

    [Fact]
    public void Honours_a_custom_tolerance()
    {
        var baseline = Report(Stat(VmBenchScenario.BootToReady, 10));
        var current = Report(Stat(VmBenchScenario.BootToReady, 10.5));

        Assert.Empty(VmBenchComparison.Compare(baseline, current, tolerance: 0.10));
        Assert.Single(VmBenchComparison.Compare(baseline, current, tolerance: 0.02));
    }

    [Fact]
    public void Skips_pairs_with_no_baseline()
    {
        var baseline = Report(Stat(VmBenchScenario.BootToReady, 10, BenchGuestOs.Windows));
        var current = Report(Stat(VmBenchScenario.BootToReady, 50, BenchGuestOs.Linux));

        Assert.Empty(VmBenchComparison.Compare(baseline, current));
    }

    [Fact]
    public void Skips_pairs_with_zero_count_on_either_side()
    {
        var baseline = Report(Stat(VmBenchScenario.BootToReady, 10, count: 0));
        var current = Report(Stat(VmBenchScenario.BootToReady, 50));

        Assert.Empty(VmBenchComparison.Compare(baseline, current));

        var baseline2 = Report(Stat(VmBenchScenario.BootToReady, 10));
        var current2 = Report(Stat(VmBenchScenario.BootToReady, 50, count: 0));

        Assert.Empty(VmBenchComparison.Compare(baseline2, current2));
    }

    [Fact]
    public void Skips_a_non_positive_baseline_median()
    {
        var baseline = Report(Stat(VmBenchScenario.Save, 0));
        var current = Report(Stat(VmBenchScenario.Save, 5));

        Assert.Empty(VmBenchComparison.Compare(baseline, current));
    }
}
