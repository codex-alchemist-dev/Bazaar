using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench.Tests;

/// <summary>
/// Statistics are checked through the runner's report, which is where they are exposed. The fake's memory samples equal
/// their 1-based call index in MiB, and calls run guest by guest (Windows first), so the Windows values are known exactly.
/// </summary>
public sealed class VmBenchStatsTests
{
    private static Task<VmBenchReport> RunAsync(FakeVmBenchHost host, VmBenchOptions options)
    {
        return new VmBenchRunner().RunAsync(host, options, "stats-host", TestContext.Current.CancellationToken);
    }

    private static VmBenchStat Find(VmBenchReport report, VmBenchScenario scenario, BenchGuestOs os)
    {
        return report.Stats.Single(s => s.Scenario == scenario && s.Os == os);
    }

    [Fact]
    public async Task EvenCount_MedianMinMaxMeanAndP95AreExact()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 20, WarmupIterations: 0, Scenarios: [VmBenchScenario.IdleRam]));

        var stat = Find(report, VmBenchScenario.IdleRam, BenchGuestOs.Windows);
        Assert.Equal("MiB", stat.Unit);
        Assert.Equal(20, stat.Count);
        Assert.Equal(0, stat.Failures);
        Assert.Equal(1, stat.Min);
        Assert.Equal(10.5, stat.Median);
        Assert.Equal(19, stat.P95);   // nearest rank: ceil(0.95 * 20) = 19th value
        Assert.Equal(20, stat.Max);
        Assert.Equal(10.5, stat.Mean);
    }

    [Fact]
    public async Task OddCount_MedianIsTheMiddleValue()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 5, WarmupIterations: 0, Scenarios: [VmBenchScenario.IdleRam]));

        var stat = Find(report, VmBenchScenario.IdleRam, BenchGuestOs.Windows);
        Assert.Equal(1, stat.Min);
        Assert.Equal(3, stat.Median);
        Assert.Equal(5, stat.P95);
        Assert.Equal(5, stat.Max);
        Assert.Equal(3, stat.Mean);
    }

    [Fact]
    public async Task FailedSamples_AreIgnoredInNumbersButCounted()
    {
        // Save call indexes 2 and 4 fail. Windows saves are calls 1..5 in seconds, so the successes are 1, 3 and 5.
        var host = new FakeVmBenchHost
        {
            FailWhen = (name, index) => name == nameof(FakeVmBenchHost.SaveAsync) && (index == 2 || index == 4),
        };
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 5, WarmupIterations: 0, Scenarios: [VmBenchScenario.Save]));

        var windows = Find(report, VmBenchScenario.Save, BenchGuestOs.Windows);
        Assert.Equal(3, windows.Count);
        Assert.Equal(2, windows.Failures);
        Assert.Equal(1, windows.Min);
        Assert.Equal(3, windows.Median);
        Assert.Equal(5, windows.P95);
        Assert.Equal(5, windows.Max);
        Assert.Equal(3, windows.Mean);

        var linux = Find(report, VmBenchScenario.Save, BenchGuestOs.Linux);
        Assert.Equal(5, linux.Count);
        Assert.Equal(0, linux.Failures);
    }

    [Fact]
    public async Task GroupWithOnlyFailures_HasZeroNumbersAndCountsFailures()
    {
        var host = new FakeVmBenchHost { ThrowOn = nameof(FakeVmBenchHost.SaveAsync) };
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 2, WarmupIterations: 0, Scenarios: [VmBenchScenario.Save]));

        var stat = Find(report, VmBenchScenario.Save, BenchGuestOs.Windows);
        Assert.Equal(0, stat.Count);
        Assert.Equal(2, stat.Failures);
        Assert.Equal(0, stat.Min);
        Assert.Equal(0, stat.Median);
        Assert.Equal(0, stat.P95);
        Assert.Equal(0, stat.Max);
        Assert.Equal(0, stat.Mean);
    }
}
