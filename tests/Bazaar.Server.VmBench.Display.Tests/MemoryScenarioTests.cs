using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class MemoryScenarioTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DesktopIdle_passes_below_and_at_the_limit_and_fails_above()
    {
        var time = new FakeTimeProvider();
        foreach (var (mib, expected) in new[] { (1000d, DisplayBenchOutcome.Passed), (2048d, DisplayBenchOutcome.Passed), (2049d, DisplayBenchOutcome.Failed) })
        {
            var s = new DesktopIdleRamScenario(new FakeTargets(), new FakeMemory(mib));

            var r = Assert.Single(await s.RunAsync(Bench.Context(time), Ct));

            Assert.Equal(expected, r.Outcome);
            Assert.Equal(mib, r.Value!.Value, 3);
            Assert.Equal(2048, r.Threshold);
            Assert.Equal("kiosk", r.Variant);
            Assert.Equal(expected == DisplayBenchOutcome.Passed, r.Reason is null);
        }
    }

    [Fact]
    public async Task DesktopIdle_waits_the_settle_time_on_the_run_clock()
    {
        var time = new FakeTimeProvider();
        var memory = new FakeMemory(500);
        var s = new DesktopIdleRamScenario(new FakeTargets(), memory);

        var task = s.RunAsync(Bench.Context(time, Bench.Criteria(c => c.SettleSeconds = 30)), Ct);

        Assert.False(task.IsCompleted);
        Assert.Equal(0, memory.Reads);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(DisplayBenchOutcome.Passed, Assert.Single(await task).Outcome);
        Assert.Equal(1, memory.Reads);
    }

    [Fact]
    public async Task DesktopIdle_is_not_run_without_ports_or_target()
    {
        var time = new FakeTimeProvider();

        var noPorts = Assert.Single(await new DesktopIdleRamScenario().RunAsync(Bench.Context(time), Ct));
        var noTarget = Assert.Single(await new DesktopIdleRamScenario(new FakeTargets { Kiosk = null }, new FakeMemory(1)).RunAsync(Bench.Context(time), Ct));

        Assert.Equal(DisplayBenchOutcome.NotRun, noPorts.Outcome);
        Assert.Null(noPorts.Value);
        Assert.False(string.IsNullOrWhiteSpace(noPorts.Reason));
        Assert.Equal(DisplayBenchOutcome.NotRun, noTarget.Outcome);
        Assert.Contains("kiosk", noTarget.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DesktopIdle_port_error_is_a_failure_not_a_pass()
    {
        var memory = new FakeMemory { Throw = new InvalidOperationException("probe broke") };

        var r = Assert.Single(await new DesktopIdleRamScenario(new FakeTargets(), memory).RunAsync(Bench.Context(new FakeTimeProvider()), Ct));

        Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome);
        Assert.Contains("probe broke", r.Reason, StringComparison.Ordinal);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task DesktopIdle_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var s = new DesktopIdleRamScenario(new FakeTargets(), new FakeMemory(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(Bench.Context(new FakeTimeProvider()), cts.Token));
    }

    [Fact]
    public async Task BrowserRam_records_the_rise_for_headless_then_headed_and_closes_each_tab()
    {
        var pool = new FakePool();
        var memory = new FakeMemory(1000, 1500, 1000, 1900);
        var s = new BrowserRamScenario(new FakeTargets(), memory, pool);

        var results = await s.RunAsync(Bench.Context(new FakeTimeProvider()), Ct);

        Assert.Equal(["headless", "headed"], results.Select(r => r.Variant));
        Assert.Equal(500, results[0].Value!.Value, 3);
        Assert.Equal(600, results[0].Threshold);
        Assert.Equal(DisplayBenchOutcome.Passed, results[0].Outcome);
        Assert.Equal(900, results[1].Value!.Value, 3);
        Assert.Equal(900, results[1].Threshold);
        Assert.Equal(DisplayBenchOutcome.Passed, results[1].Outcome);
        Assert.Equal([PoolDisplayMode.Headless, PoolDisplayMode.Headed], pool.Opened.Select(o => o.Display));
        Assert.Equal(["s1", "s2"], pool.Closed);
        Assert.Contains(results[0].Metrics, m => m.Name == "baselineMiB" && Math.Abs(m.Value - 1000) < 0.001);
        Assert.Contains(results[0].Metrics, m => m.Name == "withTabMiB" && Math.Abs(m.Value - 1500) < 0.001);
    }

    [Fact]
    public async Task BrowserRam_fails_just_over_the_limit()
    {
        var s = new BrowserRamScenario(new FakeTargets(), new FakeMemory(0, 601, 0, 901), new FakePool());

        var results = await s.RunAsync(Bench.Context(new FakeTimeProvider()), Ct);

        Assert.All(results, r => Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome));
    }

    [Fact]
    public async Task BrowserRam_is_not_run_when_a_port_is_missing_or_the_pool_refuses()
    {
        var time = new FakeTimeProvider();
        var missing = await new BrowserRamScenario(new FakeTargets(), new FakeMemory(1), null).RunAsync(Bench.Context(time), Ct);
        var refused = await new BrowserRamScenario(new FakeTargets(), new FakeMemory(1), new FakePool { RefuseWith = new BrowserPoolException(PoolFailure.NoEgress, "no proxy") })
            .RunAsync(Bench.Context(time), Ct);
        var noHost = await new BrowserRamScenario(new FakeTargets { BrowserHost = null }, new FakeMemory(1), new FakePool()).RunAsync(Bench.Context(time), Ct);

        Assert.All(missing.Concat(refused).Concat(noHost), r =>
        {
            Assert.Equal(DisplayBenchOutcome.NotRun, r.Outcome);
            Assert.Null(r.Value);
        });
        Assert.Contains("NoEgress", refused[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserRam_port_error_fails_and_still_closes_the_tab()
    {
        var pool = new FakePool();
        var failing = new BrowserRamScenario(new FakeTargets(), new ThrowingSecondRead(), pool);

        var results = await failing.RunAsync(Bench.Context(new FakeTimeProvider()), Ct);

        Assert.All(results, r => Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome));
        Assert.Equal(["s1", "s2"], pool.Closed);
    }

    [Fact]
    public async Task BrowserRam_honours_cancellation_and_closes_the_tab()
    {
        using var cts = new CancellationTokenSource();
        var pool = new FakePool();
        var memory = new CancellingMemory(cts);
        var s = new BrowserRamScenario(new FakeTargets(), memory, pool);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(Bench.Context(new FakeTimeProvider()), cts.Token));

        Assert.Equal(["s1"], pool.Closed);
    }

    private sealed class ThrowingSecondRead : IDisplayBenchMemory
    {
        private int _n;

        public Task<long> ReadGuestMemoryInUseBytesAsync(DisplayTarget target, CancellationToken ct) =>
            ++_n % 2 == 0 ? throw new InvalidOperationException("second read") : Task.FromResult(0L);
    }

    private sealed class CancellingMemory : IDisplayBenchMemory
    {
        private readonly CancellationTokenSource _cts;
        private int _n;

        public CancellingMemory(CancellationTokenSource cts) => _cts = cts;

        public Task<long> ReadGuestMemoryInUseBytesAsync(DisplayTarget target, CancellationToken ct)
        {
            if (++_n == 2)
            {
                _cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            return Task.FromResult(0L);
        }
    }
}
