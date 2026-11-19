using Bazaar.Kodo.Probes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Chaos;

/// <summary>
/// Probe fault harness (doc 46 section 17.1): a probe that hangs, throws, returns garbage, returns after
/// cancellation or flaps, and clock jumps around the breaker. No test waits on wall time except the
/// bounded safety net that turns a real hang into a failure.
/// </summary>
public sealed class ProbeChaosTests
{
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _safetyNet = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProbeRunner NewRunner(FakeTimeProvider time, int failureThreshold = 5, int slots = 2) =>
        new(new Bulkhead("local", slots, time), new CircuitBreaker(failureThreshold, TimeSpan.FromSeconds(60), time));

    [Fact]
    public async Task ProbeThatBlocksItsThreadStillTimesOut()
    {
        // A probe that does synchronous work before its first await must not hang the evaluator loop.
        FakeTimeProvider time = new();
        ProbeRunner runner = NewRunner(time);
        using ManualResetEventSlim release = new();

        Task<ProbeResult> running = Task.Run(
            () => runner.RunAsync(
                _ =>
                {
                    release.Wait(_safetyNet, CancellationToken.None);
                    return Task.FromResult(ProbeResult.Green());
                },
                _probeTimeout,
                Ct),
            Ct);

        // Advance fake time until the runner's timer exists and fires; bounded, and never waits on the wall clock.
        for (int spin = 0; spin < 100_000 && !running.IsCompleted; spin++)
        {
            time.Advance(_probeTimeout + TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        ProbeResult result = await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.Set();

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Timeout, result.Cause);
    }

    [Fact]
    public async Task NullResultIsNeverGreenAndNeverThrows()
    {
        ProbeRunner runner = NewRunner(new FakeTimeProvider());

        ProbeResult result = await runner.RunAsync(_ => Task.FromResult<ProbeResult>(null!), _probeTimeout, Ct);

        Assert.NotEqual(ProbeStatus.Green, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public async Task OutOfRangeStatusIsNeverGreenAndNeverThrows()
    {
        ProbeRunner runner = NewRunner(new FakeTimeProvider());

        ProbeResult result = await runner.RunAsync(_ => Task.FromResult(new ProbeResult((ProbeStatus)999)), _probeTimeout, Ct);

        Assert.NotEqual(ProbeStatus.Green, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public async Task NullTaskIsARedProbeException()
    {
        ProbeRunner runner = NewRunner(new FakeTimeProvider());

        ProbeResult result = await runner.RunAsync(_ => null!, _probeTimeout, Ct);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(ProbeCauses.ProbeException, result.Cause);
    }

    [Fact]
    public async Task ProbeThatReturnsAfterTimeoutDoesNotChangeTheRecordedResult()
    {
        FakeTimeProvider time = new();
        ProbeRunner runner = NewRunner(time);
        TaskCompletionSource<ProbeResult> late = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<ProbeResult> running = runner.RunAsync(_ => late.Task, _probeTimeout, Ct);
        time.Advance(_probeTimeout + TimeSpan.FromSeconds(1));
        ProbeResult result = await running.WaitAsync(_safetyNet, Ct);
        late.SetResult(ProbeResult.Green());

        Assert.Equal(ProbeCauses.Timeout, result.Cause);
    }

    [Fact]
    public async Task ThousandsOfHangingProbesTripTheBreakerInsteadOfPilingUp()
    {
        FakeTimeProvider time = new();
        ProbeRunner runner = NewRunner(time, failureThreshold: 5, slots: 1);
        int started = 0;

        for (int tick = 0; tick < 5_000; tick++)
        {
            Task<ProbeResult> running = runner.RunAsync(
                _ =>
                {
                    Interlocked.Increment(ref started);
                    return new TaskCompletionSource<ProbeResult>().Task;
                },
                _probeTimeout,
                Ct);
            time.Advance(_probeTimeout + TimeSpan.FromMilliseconds(1));
            ProbeResult result = await running.WaitAsync(_safetyNet, Ct);
            Assert.NotEqual(ProbeStatus.Green, result.Status);
        }

        // Five timeouts open the breaker; after that one trial per 60 s cooldown of (fake) time is admitted.
        double elapsedSeconds = 5_000 * (_probeTimeout.TotalSeconds + 0.001);
        Assert.True(started <= 5 + (elapsedSeconds / 60) + 1, $"{started} hanging probes were started");
    }

    [Fact]
    public async Task FlappingProbeNeverLeavesTheBreakerInAnInvalidState()
    {
        FakeTimeProvider time = new();
        CircuitBreaker breaker = new(3, TimeSpan.FromSeconds(10), time);
        ProbeRunner runner = new(new Bulkhead("local", 4, time), breaker);

        for (int tick = 0; tick < 2_000; tick++)
        {
            bool fail = tick % 3 != 0;
            ProbeResult result = await runner.RunAsync(
                _ => Task.FromResult(fail ? ProbeResult.Red("flap") : ProbeResult.Green()),
                _probeTimeout,
                Ct);
            Assert.True(result.Status is ProbeStatus.Red or ProbeStatus.Green or ProbeStatus.Unknown);
            time.Advance(TimeSpan.FromSeconds(4));
            Assert.True(breaker.State is BreakerState.Closed or BreakerState.Open or BreakerState.HalfOpen);
        }
    }

    [Fact]
    public void ClockJumpedBackwardsDoesNotKeepABreakerOpenForever()
    {
        // Cooldowns are measured on the monotonic timestamp, so a wall-clock step backwards (NTP) cannot hold a breaker open.
        SkewedTime time = new();
        CircuitBreaker breaker = new(1, TimeSpan.FromSeconds(60), time);
        Assert.True(breaker.TryEnter(out BreakerTicket ticket));
        breaker.Record(ticket, failed: true);
        Assert.Equal(BreakerState.Open, breaker.State);

        time.WallOffset = TimeSpan.FromDays(-30);
        time.Inner.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void ClockJumpedForwardsDoesNotCloseABreakerEarly()
    {
        SkewedTime time = new();
        CircuitBreaker breaker = new(1, TimeSpan.FromSeconds(60), time);
        Assert.True(breaker.TryEnter(out BreakerTicket ticket));
        breaker.Record(ticket, failed: true);

        time.WallOffset = TimeSpan.FromDays(30);
        time.Inner.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>A clock whose wall time can be stepped (NTP, VM resume) while the monotonic timestamp keeps running.</summary>
    private sealed class SkewedTime : TimeProvider
    {
        public FakeTimeProvider Inner { get; } = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

        public TimeSpan WallOffset { get; set; }

        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow() + WallOffset;

        public override long GetTimestamp() => Inner.GetTimestamp();

        public override long TimestampFrequency => Inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Inner.CreateTimer(callback, state, dueTime, period);
    }
}
