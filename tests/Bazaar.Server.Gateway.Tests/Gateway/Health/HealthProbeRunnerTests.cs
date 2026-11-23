using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Health;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Health;

public class HealthProbeRunnerTests
{
    private static readonly ProviderKey _key = new("provider-a", "secret://bazaar/providers/a/key");
    private static readonly ProviderEndpoint _endpoint = new("https://provider.test/v1", "model-x", "sk-stub-secret");
    private static readonly ProbeResult _healthy = new(true, TimeSpan.FromMilliseconds(5), "HTTP 200");
    private static readonly ProbeResult _unhealthy = new(false, TimeSpan.FromMilliseconds(5), "HTTP 500");

    private static readonly CancellationToken _ct = CancellationToken.None;

    /// <summary>A probe that answers from a delegate and counts its calls.</summary>
    private sealed class StubProbe(Func<CancellationToken, Task<ProbeResult>> answer) : IHealthProbe
    {
        public int Calls { get; private set; }

        public Task<ProbeResult> ProbeAsync(ProviderEndpoint endpoint, CancellationToken ct)
        {
            Calls++;
            return answer(ct);
        }
    }

    private static (HealthTracker Tracker, FakeClock Clock) Setup(int threshold = 2)
    {
        var clock = new FakeClock();
        return (new HealthTracker(new BreakerOptions { FailureThreshold = threshold }, clock), clock);
    }

    private static void OpenBreaker(HealthTracker tracker, int threshold)
    {
        for (var i = 0; i < threshold; i++)
        {
            tracker.Observe(_key, new ModelEvent.Failure("provider_error", "down"));
        }
    }

    [Fact]
    public async Task A_failed_probe_counts_toward_the_threshold()
    {
        var (tracker, _) = Setup(threshold: 2);
        var probe = new StubProbe(_ => Task.FromResult(_unhealthy));

        await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);
        Assert.Null(tracker.DownState(_key));

        await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);
        Assert.Equal(QuotaState.Down, tracker.DownState(_key));
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task A_healthy_probe_resets_the_failure_run()
    {
        var (tracker, _) = Setup(threshold: 2);
        var answers = new Queue<ProbeResult>([_unhealthy, _healthy, _unhealthy]);
        var probe = new StubProbe(_ => Task.FromResult(answers.Dequeue()));

        for (var i = 0; i < 3; i++)
        {
            await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);
        }

        Assert.Null(tracker.DownState(_key));
        Assert.Equal(1, tracker.GetState(_key).ConsecutiveFailures);
    }

    [Fact]
    public async Task An_open_breaker_is_not_probed_until_the_duration_elapses()
    {
        var (tracker, clock) = Setup(threshold: 2);
        OpenBreaker(tracker, 2);
        var probe = new StubProbe(_ => Task.FromResult(_healthy));

        var skipped = await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);
        Assert.Null(skipped);
        Assert.Equal(0, probe.Calls);

        clock.Advance(TimeSpan.FromSeconds(30));
        var trial = await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);

        Assert.Equal(_healthy, trial);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(BreakerState.Closed, tracker.GetState(_key).State);
    }

    [Fact]
    public async Task A_failed_half_open_probe_reopens_the_breaker()
    {
        var (tracker, clock) = Setup(threshold: 2);
        OpenBreaker(tracker, 2);
        clock.Advance(TimeSpan.FromSeconds(30));
        var probe = new StubProbe(_ => Task.FromResult(_unhealthy));

        await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);

        Assert.Equal(QuotaState.Down, tracker.DownState(_key));
    }

    [Fact]
    public async Task The_half_open_trial_limit_applies_to_probes()
    {
        var (tracker, clock) = Setup(threshold: 2);
        OpenBreaker(tracker, 2);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(tracker.TryAcquire(_key));
        var probe = new StubProbe(_ => Task.FromResult(_healthy));

        var result = await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);

        Assert.Null(result);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task A_throwing_probe_is_recorded_as_unhealthy_without_its_message()
    {
        var (tracker, _) = Setup(threshold: 1);
        var probe = new StubProbe(_ => throw new InvalidOperationException("leaked sk-stub-secret in the message"));

        var result = await HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, _ct);

        Assert.NotNull(result);
        Assert.False(result.Healthy);
        Assert.Equal("probe failed: InvalidOperationException", result.Detail);
        Assert.DoesNotContain("sk-stub-secret", result.Detail);
        Assert.Equal(result, tracker.GetState(_key).LastProbe);
        Assert.Equal(QuotaState.Down, tracker.DownState(_key));
    }

    [Fact]
    public async Task A_cancelled_half_open_probe_releases_its_trial_and_rethrows()
    {
        var (tracker, clock) = Setup(threshold: 2);
        OpenBreaker(tracker, 2);
        clock.Advance(TimeSpan.FromSeconds(30));
        var probe = new StubProbe(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_healthy);
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HealthProbeRunner.RunAsync(_key, _endpoint, probe, tracker, cts.Token));

        Assert.Equal(BreakerState.HalfOpen, tracker.GetState(_key).State);
        Assert.True(tracker.TryAcquire(_key));
    }
}
