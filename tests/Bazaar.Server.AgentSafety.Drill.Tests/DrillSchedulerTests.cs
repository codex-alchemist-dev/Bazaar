using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Drill.Tests;

/// <remarks>
/// The scheduler creates its first delay on its own thread after <c>StartAsync</c> returns, so a test cannot assume the delay exists
/// when it advances the fake clock. The helpers therefore advance in steps and wait for the result, instead of expecting one advance
/// to fire the timer.
/// </remarks>
public sealed class DrillSchedulerTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(6);

    [Fact]
    public async Task The_drill_runs_on_the_interval_and_stores_each_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var h = new DrillHarness(time);
        var store = new InMemoryDrillResultStore();
        var scheduler = Scheduler(h, store, _interval, time);

        await scheduler.StartAsync(ct);
        time.Advance(_interval - TimeSpan.FromMinutes(1));
        await Task.Delay(50, ct);
        Assert.Null(await store.LatestAsync(ct));

        var first = await AdvanceUntilAsync(time, _interval, store, _ => true, ct);
        Assert.True(first.Passed);

        var second = await AdvanceUntilAsync(time, _interval, store, r => r.Id != first.Id, ct);
        Assert.True(second.Passed);
        Assert.True(second.StartedAt > first.StartedAt);

        await scheduler.StopAsync(ct);
    }

    [Fact]
    public async Task The_interval_is_raised_to_the_one_hour_floor()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var h = new DrillHarness(time);
        var store = new InMemoryDrillResultStore();
        var scheduler = Scheduler(h, store, TimeSpan.FromMinutes(5), time);

        await scheduler.StartAsync(ct);
        time.Advance(TimeSpan.FromMinutes(30));
        await Task.Delay(50, ct);
        Assert.Null(await store.LatestAsync(ct));

        await AdvanceUntilAsync(time, TimeSpan.FromMinutes(30), store, _ => true, ct);

        await scheduler.StopAsync(ct);
    }

    [Fact]
    public async Task A_failing_drill_is_stored_and_logged_and_the_next_run_still_happens()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var h = new DrillHarness(time);
        h.Containment.ThrowWhileContained = true;
        var store = new InMemoryDrillResultStore();
        var logger = new ListLogger<DrillScheduler>();
        var scheduler = Scheduler(h, store, _interval, time, logger);

        await scheduler.StartAsync(ct);
        var failed = await AdvanceUntilAsync(time, _interval, store, _ => true, ct);
        Assert.False(failed.Passed);
        Assert.Contains(logger.Messages, m => m.Contains("containment", StringComparison.Ordinal));

        h.Containment.ThrowWhileContained = false;
        var passed = await AdvanceUntilAsync(time, _interval, store, r => r.Id != failed.Id, ct);
        Assert.True(passed.Passed);
        Assert.False(scheduler.ExecuteTask!.IsCompleted);

        await scheduler.StopAsync(ct);
    }

    [Fact]
    public async Task A_drill_that_cannot_run_is_logged_and_the_scheduler_keeps_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        var h = new DrillHarness(time);
        var store = new InMemoryDrillResultStore();
        var logger = new ListLogger<DrillScheduler>();
        var scheduler = Scheduler(h, store, _interval, time, logger);
        await h.Engine.EngageGlobalAsync("owner-device", "incident", ct);

        await scheduler.StartAsync(ct);
        await AdvanceUntilLoggedAsync(time, _interval, logger, "did not run", ct);
        Assert.Null(await store.LatestAsync(ct));
        Assert.False(scheduler.ExecuteTask!.IsCompleted);

        await h.Engine.ResumeGlobalAsync("owner-device", "incident over", ct);
        var run = await AdvanceUntilAsync(time, _interval, store, _ => true, ct);
        Assert.True(run.Passed);

        await scheduler.StopAsync(ct);
    }

    private static DrillScheduler Scheduler(
        DrillHarness h,
        IDrillResultStore store,
        TimeSpan interval,
        TimeProvider time,
        ILogger<DrillScheduler>? logger = null) =>
        new(h.Runner, store, h.Target, Options.Create(new DrillOptions { Interval = interval }), logger ?? NullLogger<DrillScheduler>.Instance, time);

    /// <summary>Advances the fake clock in steps until the store holds a run that <paramref name="done"/> accepts.</summary>
    private static async Task<DrillResult> AdvanceUntilAsync(
        FakeTimeProvider time,
        TimeSpan step,
        InMemoryDrillResultStore store,
        Func<DrillResult, bool> done,
        CancellationToken ct)
    {
        for (var i = 0; i < 200; i++)
        {
            time.Advance(step);
            await Task.Delay(10, ct);
            if (await store.LatestAsync(ct) is { } latest && done(latest))
            {
                return latest;
            }
        }

        throw new TimeoutException("The scheduler did not store a run.");
    }

    private static async Task AdvanceUntilLoggedAsync(FakeTimeProvider time, TimeSpan step, ListLogger<DrillScheduler> logger, string text, CancellationToken ct)
    {
        for (var i = 0; i < 200; i++)
        {
            time.Advance(step);
            await Task.Delay(10, ct);
            if (logger.Messages.Any(m => m.Contains(text, StringComparison.Ordinal)))
            {
                return;
            }
        }

        throw new TimeoutException("The scheduler did not log the refused drill.");
    }
}
