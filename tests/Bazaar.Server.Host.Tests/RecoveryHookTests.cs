using Bazaar.Server.Agents.Recovery;
using Bazaar.Server.Host.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class RecoveryHookTests
{
    private sealed class FakeRunner(LeaseRecoveryReport report) : ILeaseRecoveryRunner
    {
        public int Calls { get; private set; }

        public Task<LeaseRecoveryReport> RecoverAsync(CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(report);
        }
    }

    private sealed class BlockingRunner : ILeaseRecoveryRunner
    {
        public async Task<LeaseRecoveryReport> RecoverAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LeaseRecoveryReport(0, 0, 0, 0, 0);
        }
    }

    private sealed class Participant(Func<CancellationToken, Task> body) : IShutdownCheckpointParticipant
    {
        public Task CheckpointAsync(CancellationToken ct) => body(ct);
    }

    private static RecoveryHostedService Create(ILeaseRecoveryRunner runner, RecoveryHookState state, TimeSpan? timeout = null, params IShutdownCheckpointParticipant[] participants) =>
        new(runner, participants, Options.Create(new RecoveryHookOptions { ShutdownTimeout = timeout ?? TimeSpan.FromSeconds(1) }),
            state, TimeProvider.System, NullLogger<RecoveryHostedService>.Instance);

    [Fact]
    public async Task Sweep_runs_recovery_once_and_marks_ready()
    {
        var runner = new FakeRunner(new LeaseRecoveryReport(2, 2, 1, 1, 3));
        var state = new RecoveryHookState();
        var check = new RecoverySweepReadinessCheck(state);
        Assert.False((await check.CheckAsync(CancellationToken.None)).Ok);

        await Create(runner, state).StartAsync(CancellationToken.None);

        Assert.Equal(1, runner.Calls);
        Assert.Equal(3, state.LastReport!.ItemsReleased);
        Assert.True((await check.CheckAsync(CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Sweep_stops_cleanly_on_cancellation_and_stays_not_ready()
    {
        var state = new RecoveryHookState();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(new BlockingRunner(), state).StartAsync(cts.Token));

        Assert.False(state.SweepCompleted);
    }

    [Fact]
    public async Task Checkpoint_records_time_on_stop()
    {
        var state = new RecoveryHookState();
        var ran = false;
        await Create(new FakeRunner(new LeaseRecoveryReport(0, 0, 0, 0, 0)), state, null, new Participant(_ => { ran = true; return Task.CompletedTask; }))
            .StopAsync(CancellationToken.None);

        Assert.True(ran);
        Assert.NotNull(state.CheckpointAt);
    }

    [Fact]
    public async Task Checkpoint_respects_timeout_and_never_throws()
    {
        var state = new RecoveryHookState();
        var hung = new Participant(ct => Task.Delay(Timeout.Infinite, ct));
        var thrower = new Participant(_ => throw new InvalidOperationException("boom"));

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Create(new FakeRunner(new LeaseRecoveryReport(0, 0, 0, 0, 0)), state, TimeSpan.FromMilliseconds(100), thrower, hung)
            .StopAsync(CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
        Assert.NotNull(state.CheckpointAt);
    }

    [Fact]
    public async Task Checkpoint_with_already_cancelled_stop_token_does_not_throw()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var state = new RecoveryHookState();
        await Create(new FakeRunner(new LeaseRecoveryReport(0, 0, 0, 0, 0)), state, null, new Participant(ct => Task.Delay(Timeout.Infinite, ct)))
            .StopAsync(cts.Token);
        Assert.NotNull(state.CheckpointAt);
    }
}
