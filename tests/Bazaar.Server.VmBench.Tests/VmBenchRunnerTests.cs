using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench.Tests;

public sealed class VmBenchRunnerTests
{
    private static readonly int[] _oneTwoFour = [1, 2, 4];
    private static readonly int[] _oneTwo = [1, 2];

    private static Task<VmBenchReport> RunAsync(FakeVmBenchHost host, VmBenchOptions options, CancellationToken? ct = null)
    {
        return new VmBenchRunner().RunAsync(host, options, "test-host", ct ?? TestContext.Current.CancellationToken);
    }

    private static readonly Dictionary<VmBenchScenario, string> _expectedUnits = new()
    {
        [VmBenchScenario.IdleRam] = "MiB",
        [VmBenchScenario.LoadedRam] = "MiB",
        [VmBenchScenario.BootToReady] = "s",
        [VmBenchScenario.Save] = "s",
        [VmBenchScenario.Restore] = "s",
        [VmBenchScenario.Checkpoint] = "s",
        [VmBenchScenario.ShareThroughput] = "MiB/s",
        [VmBenchScenario.GodotHeadlessExport] = "s",
        [VmBenchScenario.LinuxBuild] = "s",
        [VmBenchScenario.ParallelPairs] = "s",
    };

    [Fact]
    public async Task AllScenarios_ProduceSamplesWithExpectedUnitsAndCounts()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 2, WarmupIterations: 0));

        Assert.Equal(36, report.Samples.Count);
        Assert.Equal("test-host", report.HostLabel);
        Assert.Equal(32, report.RunId.Length);
        Assert.All(report.Samples, s => Assert.Equal(_expectedUnits[s.Scenario], s.Unit));
        Assert.All(report.Samples, s => Assert.False(s.Failed));

        int CountOf(VmBenchScenario scenario, BenchGuestOs os) =>
            report.Samples.Count(s => s.Scenario == scenario && s.Os == os);

        foreach (var scenario in new[]
                 {
                     VmBenchScenario.IdleRam, VmBenchScenario.LoadedRam, VmBenchScenario.BootToReady, VmBenchScenario.Save,
                     VmBenchScenario.Restore, VmBenchScenario.Checkpoint, VmBenchScenario.ShareThroughput,
                     VmBenchScenario.GodotHeadlessExport,
                 })
        {
            Assert.Equal(2, CountOf(scenario, BenchGuestOs.Windows));
            Assert.Equal(2, CountOf(scenario, BenchGuestOs.Linux));
        }

        Assert.Equal(0, CountOf(VmBenchScenario.LinuxBuild, BenchGuestOs.Windows));
        Assert.Equal(2, CountOf(VmBenchScenario.LinuxBuild, BenchGuestOs.Linux));
        Assert.Equal(2, report.Samples.Count(s => s.Scenario == VmBenchScenario.ParallelPairs));

        // Values come from the fake: share is 128 MiB/s, a workload or boot is its seconds, RAM is the call index in MiB.
        Assert.All(report.Samples.Where(s => s.Scenario == VmBenchScenario.ShareThroughput), s => Assert.Equal(128d, s.Value));
        Assert.All(report.Samples.Where(s => s.Scenario == VmBenchScenario.GodotHeadlessExport), s => Assert.Equal(30d, s.Value));

        Assert.NotEmpty(report.Stats);
        Assert.Equal(report.Samples.Count, report.Stats.Sum(st => st.Count + st.Failures));
    }

    [Fact]
    public async Task Warmup_IterationsAreRunButDiscarded()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 3, WarmupIterations: 2, Scenarios: [VmBenchScenario.IdleRam]));

        Assert.Equal(10, host.Calls["ReadMemoryBytesAsync"]);
        Assert.Equal(6, report.Samples.Count);
        Assert.All(report.Samples, s => Assert.InRange(s.Iteration, 1, 3));
        Assert.Equal(3, report.Samples.Count(s => s.Os == BenchGuestOs.Windows));
    }

    [Fact]
    public async Task FailedStep_IsRecordedAsFailedNotAsNumber()
    {
        var host = new FakeVmBenchHost { ThrowOn = nameof(FakeVmBenchHost.SaveAsync) };
        var report = await RunAsync(host, new VmBenchOptions(Iterations: 2, WarmupIterations: 0, Scenarios: [VmBenchScenario.Save]));

        Assert.Equal(4, report.Samples.Count);
        Assert.All(report.Samples, s =>
        {
            Assert.True(s.Failed);
            Assert.Equal(0, s.Value);
            Assert.Contains("fake SaveAsync failed", s.Note);
        });
        Assert.Single(host.Retired);
    }

    [Fact]
    public async Task TimedOutStep_IsRecordedAsFailed()
    {
        var host = new FakeVmBenchHost { HangOn = nameof(FakeVmBenchHost.RunWorkloadAsync) };
        var options = new VmBenchOptions(
            Iterations: 1,
            WarmupIterations: 0,
            Scenarios: [VmBenchScenario.GodotHeadlessExport],
            StepTimeout: TimeSpan.FromMilliseconds(100));

        var report = await RunAsync(host, options);

        Assert.Equal(2, report.Samples.Count);
        Assert.All(report.Samples, s =>
        {
            Assert.True(s.Failed);
            Assert.Equal(0, s.Value);
            Assert.Contains("timed out", s.Note);
        });
        Assert.Single(host.Retired);
    }

    [Fact]
    public async Task Retire_IsCalledAfterFailure()
    {
        var host = new FakeVmBenchHost { ThrowOn = nameof(FakeVmBenchHost.SaveAsync) };
        await RunAsync(host, new VmBenchOptions(Iterations: 1, WarmupIterations: 0, Scenarios: [VmBenchScenario.Save]));

        Assert.Single(host.Retired);
        Assert.Same(host.StartedPairs[0], host.Retired[0].Guests);
        Assert.False(host.Retired[0].TokenCancelled);
    }

    [Fact]
    public async Task Retire_IsCalledAfterCancellation_WithUncancelledToken()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeVmBenchHost
        {
            OnCall = name =>
            {
                if (name == nameof(FakeVmBenchHost.ReadMemoryBytesAsync))
                {
                    cts.Cancel();
                }
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunAsync(host, new VmBenchOptions(Iterations: 3, WarmupIterations: 0, Scenarios: [VmBenchScenario.IdleRam]), cts.Token));

        Assert.Single(host.Retired);
        Assert.Same(host.StartedPairs[0], host.Retired[0].Guests);
        Assert.False(host.Retired[0].TokenCancelled);
    }

    [Fact]
    public async Task SelectedScenarios_RunOnlyThoseScenarios()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(
            Iterations: 1,
            WarmupIterations: 0,
            Scenarios: [VmBenchScenario.Save, VmBenchScenario.IdleRam]));

        Assert.Equal(
            new[] { VmBenchScenario.IdleRam, VmBenchScenario.Save },
            report.Samples.Select(s => s.Scenario).Distinct().OrderBy(s => s).ToArray());
        Assert.Equal(0, host.Calls.GetValueOrDefault("BootToReadyAsync"));
        Assert.Equal(0, host.Calls.GetValueOrDefault("StartLoadAsync"));
    }

    [Fact]
    public async Task ParallelPairs_StartsOnePairPerCountAndRetiresAll()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(
            ParallelPairCounts: [1, 2, 4],
            Scenarios: [VmBenchScenario.ParallelPairs]));

        // The main pair plus 1, 2 and 4 extra pairs.
        Assert.Equal(8, host.StartedPairs.Count);
        Assert.Equal(8, host.Retired.Count);
        Assert.All(host.Retired, r => Assert.False(r.TokenCancelled));

        Assert.Equal(_oneTwoFour, report.Samples.Select(s => s.Iteration).ToArray());
        Assert.All(report.Samples, s =>
        {
            Assert.Equal(VmBenchScenario.ParallelPairs, s.Scenario);
            Assert.Equal("s", s.Unit);
            Assert.False(s.Failed);
        });
    }

    [Fact]
    public async Task ParallelPairs_DefaultCountsAreOneAndTwo()
    {
        var host = new FakeVmBenchHost();
        var report = await RunAsync(host, new VmBenchOptions(Scenarios: [VmBenchScenario.ParallelPairs]));

        Assert.Equal(_oneTwo, report.Samples.Select(s => s.Iteration).ToArray());
        Assert.Equal(4, host.StartedPairs.Count);
    }

    [Fact]
    public async Task LoadedRam_StartLoadFailure_RecordsEveryMeasuredIterationAsFailed()
    {
        var host = new FakeVmBenchHost { ThrowOn = nameof(FakeVmBenchHost.StartLoadAsync) };
        var report = await RunAsync(host, new VmBenchOptions(
            Iterations: 2,
            WarmupIterations: 0,
            Scenarios: [VmBenchScenario.LoadedRam]));

        Assert.Equal(4, report.Samples.Count);
        Assert.All(report.Samples, s => Assert.True(s.Failed));
        Assert.Equal(0, host.Calls.GetValueOrDefault("ReadMemoryBytesAsync"));
    }
}
