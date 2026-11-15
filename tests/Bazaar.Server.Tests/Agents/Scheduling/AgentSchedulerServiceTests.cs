using System.Threading.Channels;
using Bazaar.Server.Agents.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>Tests for the hosted wake loop with a fake scheduler and a fake clock.</summary>
public sealed class AgentSchedulerServiceTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AgentSchedulerService Service(IAgentScheduler scheduler) => new(
        scheduler,
        new AgentSchedulerOptions { Enabled = true, Route = "route-1", PollInterval = _interval },
        _time,
        NullLogger<AgentSchedulerService>.Instance);

    [Fact]
    public async Task It_runs_a_pass_at_start_and_one_more_at_every_tick()
    {
        var scheduler = new FakeScheduler();
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        Assert.Equal(1, await scheduler.NextPassAsync(Ct));

        _time.Advance(_interval);
        Assert.Equal(2, await scheduler.NextPassAsync(Ct));

        _time.Advance(_interval);
        Assert.Equal(3, await scheduler.NextPassAsync(Ct));

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task No_pass_runs_before_the_interval_has_passed()
    {
        var scheduler = new FakeScheduler();
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        await scheduler.NextPassAsync(Ct);
        _time.Advance(_interval - TimeSpan.FromMilliseconds(1));
        await service.StopAsync(Ct);

        Assert.Equal(1, scheduler.Passes);
    }

    [Fact]
    public async Task A_throwing_pass_does_not_stop_the_loop()
    {
        var scheduler = new FakeScheduler { OnPass = (pass, _) => pass == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask };
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        Assert.Equal(1, await scheduler.NextPassAsync(Ct));
        _time.Advance(_interval);
        Assert.Equal(2, await scheduler.NextPassAsync(Ct));

        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task A_pass_that_fails_with_its_own_cancellation_does_not_stop_the_loop()
    {
        var scheduler = new FakeScheduler { OnPass = (pass, _) => pass == 1 ? throw new OperationCanceledException("a timeout") : Task.CompletedTask };
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        await scheduler.NextPassAsync(Ct);
        _time.Advance(_interval);
        Assert.Equal(2, await scheduler.NextPassAsync(Ct));

        await service.StopAsync(Ct);
    }

    [Fact]
    public async Task Stopping_between_passes_ends_the_loop_cleanly()
    {
        var scheduler = new FakeScheduler();
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        await scheduler.NextPassAsync(Ct);
        await service.StopAsync(Ct);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        _time.Advance(_interval);
        Assert.Equal(1, scheduler.Passes);
    }

    [Fact]
    public async Task Stopping_cancels_the_pass_in_flight_and_ends_the_loop_cleanly()
    {
        var scheduler = new FakeScheduler { OnPass = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        using var service = Service(scheduler);

        await service.StartAsync(Ct);
        await scheduler.NextPassAsync(Ct);
        await service.StopAsync(Ct);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    /// <summary>Counts passes, announces each one as it starts, and then does what <see cref="OnPass"/> says.</summary>
    private sealed class FakeScheduler : IAgentScheduler
    {
        private readonly Channel<int> _started = Channel.CreateUnbounded<int>();
        private int _passes;

        public Func<int, CancellationToken, Task> OnPass { get; init; } = (_, _) => Task.CompletedTask;

        public int Passes => Volatile.Read(ref _passes);

        /// <summary>Waits for the next pass to start and returns its number, counted from 1.</summary>
        public async Task<int> NextPassAsync(CancellationToken ct) => await _started.Reader.ReadAsync(ct);

        public async Task<AgentSchedulerPass> RunOnceAsync(CancellationToken ct)
        {
            var pass = Interlocked.Increment(ref _passes);
            _started.Writer.TryWrite(pass);
            await OnPass(pass, ct);
            return new AgentSchedulerPass(1, 1, 0, 0, 0, 0);
        }
    }
}
