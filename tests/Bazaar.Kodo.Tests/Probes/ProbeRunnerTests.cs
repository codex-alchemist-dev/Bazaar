using Bazaar.Kodo.Probes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Probes;

public sealed class ProbeRunnerTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenBreakerYieldsUnknownBreakerOpenWithoutRunningTheProbe()
    {
        var time = new FakeTimeProvider();
        var runner = NewRunner(time);

        for (var i = 0; i < CircuitBreaker.DefaultFailureThreshold; i++)
        {
            ProbeResult failed = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Red("down")), ProbeTimeout, Ct);
            Assert.Equal(ProbeStatus.Red, failed.Status);
        }

        var ran = false;
        ProbeResult result = await runner.RunAsync(
            _ =>
            {
                ran = true;
                return Task.FromResult(ProbeResult.Green());
            },
            ProbeTimeout, Ct);

        Assert.False(ran);
        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.BreakerOpen, result.Cause);
    }

    [Fact]
    public async Task ExceptionIsRecordedAsRedAndCountsAsAFailure()
    {
        var runner = NewRunner(new FakeTimeProvider(), failureThreshold: 1);

        ProbeResult result = await runner.RunAsync(_ => throw new IOException("reset"), ProbeTimeout, Ct);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(ProbeCauses.ProbeException, result.Cause);
        Assert.IsType<IOException>(result.Error);

        ProbeResult next = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct);
        Assert.Equal(ProbeCauses.BreakerOpen, next.Cause);
    }

    [Fact]
    public async Task TimeoutIsUnknownAndCountsAsAFailure()
    {
        var time = new FakeTimeProvider();
        var runner = NewRunner(time, failureThreshold: 1);

        Task<ProbeResult> running = runner.RunAsync(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ProbeResult.Green();
            },
            TimeSpan.FromSeconds(2), Ct);
        time.Advance(TimeSpan.FromSeconds(2));
        ProbeResult timedOut = await running;

        Assert.Equal(ProbeStatus.Unknown, timedOut.Status);
        Assert.Equal(ProbeCauses.Timeout, timedOut.Cause);

        ProbeResult next = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct);
        Assert.Equal(ProbeCauses.BreakerOpen, next.Cause);
    }

    [Fact]
    public async Task PassingTrialAfterCooldownClosesTheBreaker()
    {
        var time = new FakeTimeProvider();
        var runner = NewRunner(time, failureThreshold: 1);
        await runner.RunAsync(_ => Task.FromResult(ProbeResult.Red("down")), ProbeTimeout, Ct);

        time.Advance(CircuitBreaker.DefaultCooldown);
        ProbeResult trial = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct);

        Assert.Equal(ProbeStatus.Green, trial.Status);
        Assert.Equal(ProbeStatus.Green, (await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct)).Status);
    }

    [Fact]
    public async Task FullPoolReleasesTheTrialWithoutCountingAFailure()
    {
        var time = new FakeTimeProvider();
        var bulkhead = new Bulkhead("external", maxConcurrency: 1, timeProvider: time);
        var breaker = new CircuitBreaker(failureThreshold: 1, timeProvider: time);
        var runner = new ProbeRunner(bulkhead, breaker);
        await runner.RunAsync(_ => Task.FromResult(ProbeResult.Red("down")), ProbeTimeout, Ct);
        time.Advance(CircuitBreaker.DefaultCooldown);

        // Another caller holds the only slot, so the trial cannot run.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProbeResult> holder = bulkhead.RunAsync(
            async _ =>
            {
                await release.Task;
                return ProbeResult.Green();
            },
            ProbeTimeout, Ct);

        ProbeResult full = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct);
        Assert.Equal(ProbeCauses.BulkheadFull, full.Cause);
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        release.SetResult();
        await holder;

        // The trial slot was released, so the next caller gets the trial and closes the breaker.
        ProbeResult trial = await runner.RunAsync(_ => Task.FromResult(ProbeResult.Green()), ProbeTimeout, Ct);
        Assert.Equal(ProbeStatus.Green, trial.Status);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    private static ProbeRunner NewRunner(TimeProvider time, int failureThreshold = CircuitBreaker.DefaultFailureThreshold)
    {
        return new ProbeRunner(
            new Bulkhead("external", maxConcurrency: 4, timeProvider: time),
            new CircuitBreaker(failureThreshold, timeProvider: time));
    }
}
