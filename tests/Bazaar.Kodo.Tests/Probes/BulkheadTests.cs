using Bazaar.Kodo.Probes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Probes;

public sealed class BulkheadTests
{
    [Fact]
    public async Task ReturnsTheProbeResult()
    {
        var bulkhead = new Bulkhead("local", maxConcurrency: 2);

        ProbeResult result = await bulkhead.RunAsync(_ => Task.FromResult(ProbeResult.Green()), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeResult.Green(), result);
        Assert.Equal(2, bulkhead.Available);
    }

    [Fact]
    public async Task ExceptionBecomesRedWithCauseProbeException()
    {
        var bulkhead = new Bulkhead("external", maxConcurrency: 1);
        var boom = new InvalidOperationException("boom");

        ProbeResult result = await bulkhead.RunAsync(
            _ => throw boom,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(ProbeCauses.ProbeException, result.Cause);
        Assert.Same(boom, result.Error);
        Assert.Equal(1, bulkhead.Available);
    }

    [Fact]
    public async Task TimeoutReturnsUnknownWithCauseTimeoutAndReleasesTheSlot()
    {
        var time = new FakeTimeProvider();
        var bulkhead = new Bulkhead("slow", maxConcurrency: 1, timeProvider: time);

        Task<ProbeResult> running = bulkhead.RunAsync(
            ct => HangUntilCancelled(ct),
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(10));
        ProbeResult result = await running;

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Timeout, result.Cause);
        Assert.Equal(1, bulkhead.Available);
    }

    [Fact]
    public async Task FullPoolReturnsUnknownWithoutRunningTheProbe()
    {
        var bulkhead = new Bulkhead("db", maxConcurrency: 1, timeProvider: new FakeTimeProvider());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<ProbeResult> holder = bulkhead.RunAsync(
            async _ =>
            {
                await release.Task;
                return ProbeResult.Green();
            },
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var ranSecond = false;
        ProbeResult second = await bulkhead.RunAsync(
            _ =>
            {
                ranSecond = true;
                return Task.FromResult(ProbeResult.Green());
            },
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(ranSecond);
        Assert.Equal(ProbeStatus.Unknown, second.Status);
        Assert.Equal(ProbeCauses.BulkheadFull, second.Cause);

        release.SetResult();
        Assert.Equal(ProbeStatus.Green, (await holder).Status);
        Assert.Equal(1, bulkhead.Available);
    }

    [Fact]
    public async Task CallerCancellationIsThrownNotReturnedAsAProbeResult()
    {
        var bulkhead = new Bulkhead("local", maxConcurrency: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bulkhead.RunAsync(_ => Task.FromResult(ProbeResult.Green()), TimeSpan.FromSeconds(1), cts.Token));

        Assert.Equal(1, bulkhead.Available);
    }

    [Fact]
    public void RejectsAPoolWithNoSlots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bulkhead("local", maxConcurrency: 0));
    }

    private static async Task<ProbeResult> HangUntilCancelled(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return ProbeResult.Green();
    }
}
