using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.Readiness;

public sealed class TunnelReadinessTests
{
    private static readonly TunnelReadinessOptions _options = TunnelReadinessOptions.Default;

    /// <summary>Advances the fake clock whenever the code under test waits on it.</summary>
    private static async Task<TunnelReadinessResult> RunAsync(FakeTimeProvider time, Func<CancellationToken, Task<bool>> attempt)
    {
        var run = TunnelReadiness.WaitAsync(attempt, _options, time, TestContext.Current.CancellationToken);
        while (!run.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        return await run;
    }

    [Fact]
    public async Task Succeeds_at_once_when_the_first_try_works()
    {
        var result = await RunAsync(new FakeTimeProvider(), _ => Task.FromResult(true));

        Assert.True(result.Reachable);
        Assert.Equal(1, result.Attempts);
        Assert.Null(result.LastError);
    }

    [Fact]
    public async Task Keeps_trying_past_a_minute_until_the_tunnel_answers()
    {
        var time = new FakeTimeProvider();
        var ready = time.GetUtcNow() + TimeSpan.FromSeconds(65);

        var result = await RunAsync(time, _ => Task.FromResult(time.GetUtcNow() >= ready));

        Assert.True(result.Reachable);
        Assert.InRange(result.Elapsed, TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(80));
    }

    [Fact]
    public async Task Gives_up_after_the_window_with_the_last_error()
    {
        var result = await RunAsync(new FakeTimeProvider(), _ => throw new InvalidOperationException("404"));

        Assert.False(result.Reachable);
        Assert.Equal("404", result.LastError);
        Assert.InRange(result.Elapsed, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(92));
        Assert.InRange(result.Attempts, 9, 14);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var time = new FakeTimeProvider();
        var run = TunnelReadiness.WaitAsync(_ => Task.FromResult(false), _options, time, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
