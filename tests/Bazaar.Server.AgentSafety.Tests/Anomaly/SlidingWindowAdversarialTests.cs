using Bazaar.Server.AgentSafety.Anomaly;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Tests.Anomaly;

/// <summary>One agent flooding keys, or sending far-future times, must not blind the rule to another agent.</summary>
public sealed class SlidingWindowAdversarialTests
{
    private static readonly WindowedRuleOptions _threeInAMinute = new(3, TimeSpan.FromMinutes(1), AnomalySeverity.High);

    // The shipped key limit of one rule (SlidingWindowCounter.DefaultMaxKeys, which is internal).
    private const int _tableSize = 4096;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    private static AnomalyEvent Read(string agent, string objectClass, DateTimeOffset at) =>
        new(AnomalyEventKind.ObjectRead, agent, null, objectClass, true, at);

    [Fact]
    public void One_agents_flood_of_object_classes_cannot_evict_another_agents_counters()
    {
        var rule = new ReadBurstRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();
        Assert.Empty(rule.Evaluate(Read("victim", "contacts", now)));
        Assert.Empty(rule.Evaluate(Read("victim", "contacts", now)));

        // Far more distinct keys than the whole table holds, from one agent.
        for (var i = 0; i < _tableSize * 2; i++)
        {
            rule.Evaluate(Read("attacker", "class-" + i, now));
        }

        Assert.Single(rule.Evaluate(Read("victim", "contacts", now)));
    }

    [Fact]
    public void Many_agents_each_within_their_cap_still_cannot_push_out_an_agent_close_to_its_threshold()
    {
        var rule = new ReadBurstRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();
        rule.Evaluate(Read("victim", "contacts", now));
        rule.Evaluate(Read("victim", "contacts", now));

        for (var i = 0; i < _tableSize + 500; i++)
        {
            rule.Evaluate(Read("noise-" + i, "contacts", now));
        }

        Assert.Single(rule.Evaluate(Read("victim", "contacts", now)));
    }

    [Fact]
    public void A_far_future_timestamp_does_not_reset_the_window()
    {
        var rule = new ApprovalFloodRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();
        Assert.Empty(rule.Evaluate(Approval("a", now)));
        Assert.Empty(rule.Evaluate(Approval("a", now)));

        // A time a year ahead is counted at the clock's now, so the third event still fills the window.
        Assert.Single(rule.Evaluate(Approval("a", now.AddYears(1))));
    }

    [Fact]
    public void A_far_future_timestamp_does_not_silence_later_alerts_for_the_agent()
    {
        var rule = new ApprovalFloodRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();
        for (var i = 0; i < 3; i++)
        {
            rule.Evaluate(Approval("a", now.AddYears(1)));
        }

        // The alert fired at the clock's now, so after a window it can fire again.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var later = _clock.GetUtcNow();
        rule.Evaluate(Approval("a", later));
        rule.Evaluate(Approval("a", later));
        Assert.Single(rule.Evaluate(Approval("a", later)));
    }

    [Fact]
    public void A_small_forward_skew_is_kept()
    {
        var rule = new ApprovalFloodRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();

        rule.Evaluate(Approval("a", now));
        rule.Evaluate(Approval("a", now));
        var alert = Assert.Single(rule.Evaluate(Approval("a", now.AddSeconds(10))));

        Assert.Equal(now.AddSeconds(10), alert.At);
    }

    [Fact]
    public async Task The_engine_clamps_a_far_future_event_time_on_the_alert()
    {
        var sink = new ChannelAnomalyAlertSource();
        var engine = new AnomalyEngine([new CanaryHitRule(new SeverityRuleOptions(AnomalySeverity.Critical))],
            sink, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnomalyEngine>.Instance, time: _clock);

        await engine.HandleAsync(new AnomalyEvent(AnomalyEventKind.CanaryTouched, "a", null, "c", true, _clock.GetUtcNow().AddYears(5)), TestContext.Current.CancellationToken);

        await foreach (var alert in sink.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(_clock.GetUtcNow(), alert.At);
            break;
        }
    }

    private static AnomalyEvent Approval(string agent, DateTimeOffset at) => new(AnomalyEventKind.ApprovalRequested, agent, null, null, true, at);
}
