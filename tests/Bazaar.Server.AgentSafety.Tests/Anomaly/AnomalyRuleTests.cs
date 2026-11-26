using System.Collections.Concurrent;
using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Testing;

namespace Bazaar.Server.AgentSafety.Tests.Anomaly;

public sealed class AnomalyRuleTests
{
    private static readonly WindowedRuleOptions _threeInAMinute = new(3, TimeSpan.FromMinutes(1), AnomalySeverity.High);

    private static DateTimeOffset At(int seconds) => FakeClock.DefaultStart.AddSeconds(seconds);

    private static AnomalyEvent Read(string agent, int seconds, string? objectClass = "contacts") =>
        new(AnomalyEventKind.ObjectRead, agent, "session-1", objectClass, InScope: true, At(seconds));

    private static AnomalyEvent Approval(string agent, int seconds) =>
        new(AnomalyEventKind.ApprovalRequested, agent, "session-1", null, InScope: true, At(seconds));

    [Fact]
    public void Read_burst_fires_at_the_threshold_and_not_below()
    {
        var rule = new ReadBurstRule(_threeInAMinute);

        Assert.Empty(rule.Evaluate(Read("agent-a", 0)));
        Assert.Empty(rule.Evaluate(Read("agent-a", 1)));

        var alert = Assert.Single(rule.Evaluate(Read("agent-a", 2)));
        Assert.Equal("read_burst", alert.RuleId);
        Assert.Equal("agent-a", alert.AgentId);
        Assert.Equal("session-1", alert.SessionId);
        Assert.Equal(AnomalySeverity.High, alert.Severity);
        Assert.Equal(At(2), alert.At);
    }

    [Fact]
    public void Read_burst_counts_only_events_inside_the_window()
    {
        var rule = new ReadBurstRule(_threeInAMinute);

        // The first event is 61 seconds before the third, so the three are not inside one minute.
        Assert.Empty(rule.Evaluate(Read("agent-a", 0)));
        Assert.Empty(rule.Evaluate(Read("agent-a", 30)));
        Assert.Empty(rule.Evaluate(Read("agent-a", 61)));

        // The window has rolled to 30, 61, 62, which are inside one minute.
        Assert.Single(rule.Evaluate(Read("agent-a", 62)));
    }

    [Fact]
    public void Read_burst_stays_quiet_for_one_window_after_it_fires()
    {
        var rule = new ReadBurstRule(new WindowedRuleOptions(2, TimeSpan.FromMinutes(1), AnomalySeverity.High));

        Assert.Empty(rule.Evaluate(Read("agent-a", 0)));
        Assert.Single(rule.Evaluate(Read("agent-a", 1)));

        // The burst continues inside the window: one alert only.
        Assert.Empty(rule.Evaluate(Read("agent-a", 2)));
        Assert.Empty(rule.Evaluate(Read("agent-a", 3)));
        Assert.Empty(rule.Evaluate(Read("agent-a", 30)));

        // Once a full window has passed since the alert, the rule can fire again.
        Assert.Single(rule.Evaluate(Read("agent-a", 61)));
    }

    [Fact]
    public void Read_burst_keeps_each_object_class_apart()
    {
        var rule = new ReadBurstRule(new WindowedRuleOptions(2, TimeSpan.FromMinutes(1), AnomalySeverity.High));

        Assert.Empty(rule.Evaluate(Read("agent-a", 0, "contacts")));
        Assert.Empty(rule.Evaluate(Read("agent-a", 1, "mail")));

        Assert.Single(rule.Evaluate(Read("agent-a", 2, "contacts")));
    }

    [Fact]
    public void Read_burst_keeps_each_agent_apart()
    {
        var rule = new ReadBurstRule(new WindowedRuleOptions(2, TimeSpan.FromMinutes(1), AnomalySeverity.High));

        Assert.Empty(rule.Evaluate(Read("agent-a", 0)));
        Assert.Empty(rule.Evaluate(Read("agent-b", 1)));

        var alert = Assert.Single(rule.Evaluate(Read("agent-a", 2)));
        Assert.Equal("agent-a", alert.AgentId);
    }

    [Fact]
    public void Read_burst_ignores_other_event_kinds()
    {
        var rule = new ReadBurstRule(new WindowedRuleOptions(1, TimeSpan.FromMinutes(1), AnomalySeverity.High));

        Assert.Empty(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.ScopedAccess, "agent-a", null, "contacts", InScope: true, At(0))));
        Assert.Empty(rule.Evaluate(Approval("agent-a", 1)));
    }

    [Fact]
    public async Task Read_burst_loses_no_count_under_parallel_events()
    {
        const int Events = 1000;
        var rule = new ReadBurstRule(new WindowedRuleOptions(Events, TimeSpan.FromMinutes(1), AnomalySeverity.High));
        var alerts = new ConcurrentBag<AnomalyAlert>();

        await Task.Run(() => Parallel.For(0, Events, _ =>
        {
            foreach (var alert in rule.Evaluate(Read("agent-a", 0)))
            {
                alerts.Add(alert);
            }
        }), TestContext.Current.CancellationToken);

        // The count reaches the threshold exactly once, so exactly one alert comes out.
        Assert.Single(alerts);
    }

    [Fact]
    public void Out_of_scope_read_fires_on_each_out_of_scope_access_only()
    {
        var rule = new OutOfScopeReadRule(new SeverityRuleOptions(AnomalySeverity.High));

        Assert.Empty(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.ScopedAccess, "agent-a", "session-1", "mail", InScope: true, At(0))));
        Assert.Empty(rule.Evaluate(Read("agent-a", 1)));

        var first = Assert.Single(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.ScopedAccess, "agent-a", "session-1", "mail", InScope: false, At(2))));
        Assert.Equal("out_of_scope_read", first.RuleId);
        Assert.Equal(AnomalySeverity.High, first.Severity);
        Assert.Single(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.ScopedAccess, "agent-a", "session-1", "mail", InScope: false, At(3))));
    }

    [Fact]
    public void Canary_hit_fires_on_every_touch_at_critical_by_default()
    {
        var rule = new CanaryHitRule(new AnomalyRuleOptions().CanaryHit);

        Assert.Empty(rule.Evaluate(Read("agent-a", 0)));

        var first = Assert.Single(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.CanaryTouched, "agent-a", "session-1", null, InScope: true, At(1))));
        Assert.Equal("canary_hit", first.RuleId);
        Assert.Equal(AnomalySeverity.Critical, first.Severity);
        Assert.Single(rule.Evaluate(new AnomalyEvent(AnomalyEventKind.CanaryTouched, "agent-a", "session-1", null, InScope: true, At(2))));
    }

    [Fact]
    public void Approval_flood_fires_at_the_threshold_and_not_below()
    {
        var rule = new ApprovalFloodRule(_threeInAMinute);

        Assert.Empty(rule.Evaluate(Approval("agent-a", 0)));
        Assert.Empty(rule.Evaluate(Approval("agent-a", 1)));

        var alert = Assert.Single(rule.Evaluate(Approval("agent-a", 2)));
        Assert.Equal("approval_flood", alert.RuleId);
        Assert.Equal(AnomalySeverity.High, alert.Severity);
    }

    [Fact]
    public void Approval_flood_rolls_its_window_and_stays_quiet_during_cooldown()
    {
        var rule = new ApprovalFloodRule(new WindowedRuleOptions(2, TimeSpan.FromMinutes(1), AnomalySeverity.Medium));

        Assert.Empty(rule.Evaluate(Approval("agent-a", 0)));
        Assert.Single(rule.Evaluate(Approval("agent-a", 1)));
        Assert.Empty(rule.Evaluate(Approval("agent-a", 2)));

        // Requests that are a minute apart do not count together.
        Assert.Empty(rule.Evaluate(Approval("agent-a", 100)));
        Assert.Single(rule.Evaluate(Approval("agent-a", 101)));
    }

    [Fact]
    public void Approval_flood_keeps_each_agent_apart()
    {
        var rule = new ApprovalFloodRule(new WindowedRuleOptions(2, TimeSpan.FromMinutes(1), AnomalySeverity.Medium));

        Assert.Empty(rule.Evaluate(Approval("agent-a", 0)));
        Assert.Empty(rule.Evaluate(Approval("agent-b", 1)));
        Assert.Single(rule.Evaluate(Approval("agent-b", 2)));
    }

    [Fact]
    public async Task Approval_flood_loses_no_count_under_parallel_events()
    {
        const int Events = 500;
        var rule = new ApprovalFloodRule(new WindowedRuleOptions(Events, TimeSpan.FromMinutes(1), AnomalySeverity.Medium));
        var alerts = new ConcurrentBag<AnomalyAlert>();

        await Task.Run(() => Parallel.For(0, Events, _ =>
        {
            foreach (var alert in rule.Evaluate(Approval("agent-a", 0)))
            {
                alerts.Add(alert);
            }
        }), TestContext.Current.CancellationToken);

        Assert.Single(alerts);
    }

    [Fact]
    public void Rules_reject_invalid_options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadBurstRule(new WindowedRuleOptions(0, TimeSpan.FromMinutes(1), AnomalySeverity.High)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadBurstRule(new WindowedRuleOptions(WindowedRuleOptions.MaxThreshold + 1, TimeSpan.FromMinutes(1), AnomalySeverity.High)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApprovalFloodRule(new WindowedRuleOptions(1, TimeSpan.FromMilliseconds(999), AnomalySeverity.Medium)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApprovalFloodRule(new WindowedRuleOptions(1, TimeSpan.FromDays(8), AnomalySeverity.Medium)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanaryHitRule(new SeverityRuleOptions((AnomalySeverity)99)));
        Assert.Throws<ArgumentNullException>(() => new OutOfScopeReadRule(null!));
    }

    [Fact]
    public void Default_options_are_valid()
    {
        var options = new AnomalyRuleOptions().Validate();

        Assert.Equal(AnomalySeverity.Critical, options.CanaryHit.Severity);
        Assert.Equal(AnomalySeverity.High, options.OutOfScopeRead.Severity);
        Assert.True(options.ReadBurst.Threshold > 0);
        Assert.True(options.ApprovalFlood.Threshold > 0);
    }

    [Fact]
    public void Options_validation_names_the_bad_setting()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AnomalyRuleOptions { ApprovalFlood = new WindowedRuleOptions(0, TimeSpan.FromMinutes(1), AnomalySeverity.Low) }.Validate());

        Assert.Equal("ApprovalFlood.Threshold", error.ParamName);
    }
}
