using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Health;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gateway.Health;

public class HealthTrackerTests
{
    private static readonly ProviderKey _keyA = new("provider-a", "secret://bazaar/providers/a/key");
    private static readonly ProviderKey _keyB = new("provider-b", "secret://bazaar/providers/b/key");

    private static readonly ModelEvent _done = new ModelEvent.Done(StopReason.EndTurn, "served-model");
    private static readonly ModelEvent _providerError = new ModelEvent.Failure("provider_error", "The upstream returned HTTP 500.");

    private static HealthTracker Tracker(FakeClock clock, int threshold = 5) =>
        new(new BreakerOptions { FailureThreshold = threshold }, clock);

    private static void Observe(HealthTracker tracker, ProviderKey key, ModelEvent modelEvent, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            tracker.Observe(key, modelEvent);
        }
    }

    [Theory]
    [InlineData("provider_error", true)]
    [InlineData("timeout", true)]
    [InlineData("stream_dropped", true)]
    [InlineData("rate_limited", false)]
    [InlineData("spend_capped", false)]
    [InlineData("malformed_tool_json", false)]
    [InlineData("some_unknown_code", false)]
    public void Classify_counts_only_provider_down_failures(string code, bool countsAsHealthFailure)
    {
        Assert.Equal(countsAsHealthFailure, HealthTracker.Classify(new ModelEvent.Failure(code, "message")));
    }

    [Fact]
    public void Each_provider_key_has_its_own_breaker()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 2);

        Observe(tracker, _keyA, _providerError, 2);

        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));
        Assert.False(tracker.IsAvailable(_keyA));
        Assert.Null(tracker.DownState(_keyB));
        Assert.True(tracker.IsAvailable(_keyB));
    }

    [Fact]
    public void Two_key_references_on_one_provider_are_tracked_separately()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 2);
        var first = new ProviderKey("provider-a", "key-1");
        var second = new ProviderKey("provider-a", "key-2");

        Observe(tracker, first, _providerError, 2);

        Assert.Equal(BreakerState.Open, tracker.GetState(first).State);
        Assert.Equal(BreakerState.Closed, tracker.GetState(second).State);
    }

    [Fact]
    public void A_done_event_resets_the_passive_failure_run()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock);

        Observe(tracker, _keyA, _providerError, 4);
        Observe(tracker, _keyA, _done);
        Observe(tracker, _keyA, _providerError, 4);
        Assert.Null(tracker.DownState(_keyA));

        Observe(tracker, _keyA, _providerError);
        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));
    }

    [Fact]
    public void Quota_and_model_failures_never_open_the_breaker()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);

        Observe(tracker, _keyA, new ModelEvent.Failure("rate_limited", "limit", 30), 10);
        Observe(tracker, _keyA, new ModelEvent.Failure("spend_capped", "cap"), 10);
        Observe(tracker, _keyA, new ModelEvent.Failure("malformed_tool_json", "bad"), 10);

        Assert.Null(tracker.DownState(_keyA));
        Assert.True(tracker.IsAvailable(_keyA));
        Assert.Equal(0, tracker.GetState(_keyA).ConsecutiveFailures);
    }

    [Fact]
    public void A_quota_failure_does_not_reset_the_provider_failure_run()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock);

        Observe(tracker, _keyA, _providerError, 4);
        Observe(tracker, _keyA, new ModelEvent.Failure("rate_limited", "limit"));
        Observe(tracker, _keyA, _providerError);

        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));
    }

    [Fact]
    public void Timeouts_and_dropped_streams_count_as_failures()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 2);

        Observe(tracker, _keyA, new ModelEvent.Failure("timeout", "slow"));
        Observe(tracker, _keyA, new ModelEvent.Failure("stream_dropped", "reset"));

        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));
    }

    [Fact]
    public void Down_clears_when_the_open_duration_passes()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);
        Observe(tracker, _keyA, _providerError);
        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Null(tracker.DownState(_keyA));
        Assert.True(tracker.IsAvailable(_keyA));
    }

    [Fact]
    public void A_done_event_on_a_half_open_trial_closes_the_breaker()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);
        Observe(tracker, _keyA, _providerError);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(tracker.TryAcquire(_keyA));
        Observe(tracker, _keyA, _done);

        Assert.Equal(BreakerState.Closed, tracker.GetState(_keyA).State);
    }

    [Fact]
    public void A_provider_error_on_a_half_open_trial_reopens_the_breaker()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);
        Observe(tracker, _keyA, _providerError);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(tracker.TryAcquire(_keyA));
        Observe(tracker, _keyA, _providerError);

        Assert.Equal(QuotaState.Down, tracker.DownState(_keyA));
    }

    [Fact]
    public void A_quota_failure_on_a_half_open_trial_releases_the_slot_without_a_verdict()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);
        Observe(tracker, _keyA, _providerError);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(tracker.TryAcquire(_keyA));
        Observe(tracker, _keyA, new ModelEvent.Failure("rate_limited", "limit"));

        Assert.Equal(BreakerState.HalfOpen, tracker.GetState(_keyA).State);
        Assert.True(tracker.TryAcquire(_keyA));
    }

    [Fact]
    public void Non_terminal_events_are_ignored()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);

        Observe(tracker, _keyA, new ModelEvent.TextDelta("hello"));
        Observe(tracker, _keyA, new ModelEvent.UsageReported(new GatewayTokenUsage(1, 0, 0, 0, 1, 0)));

        Assert.Equal(BreakerState.Closed, tracker.GetState(_keyA).State);
    }

    [Fact]
    public void The_snapshot_carries_the_last_probe_result_and_change_time()
    {
        var clock = new FakeClock();
        var tracker = Tracker(clock, threshold: 1);
        Observe(tracker, _keyA, _providerError);
        var probe = new ProbeResult(false, TimeSpan.FromMilliseconds(12), "HTTP 500");

        tracker.RecordProbe(_keyA, probe);

        var snapshot = tracker.GetState(_keyA);
        Assert.Equal(BreakerState.Open, snapshot.State);
        Assert.Equal(FakeClock.DefaultStart, snapshot.LastChangedAt);
        Assert.Equal(probe, snapshot.LastProbe);
    }

    [Fact]
    public void An_unseen_key_reads_as_closed_with_no_probe()
    {
        var tracker = Tracker(new FakeClock());

        var snapshot = tracker.GetState(_keyA);

        Assert.Equal(BreakerState.Closed, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Null(snapshot.LastProbe);
    }
}
