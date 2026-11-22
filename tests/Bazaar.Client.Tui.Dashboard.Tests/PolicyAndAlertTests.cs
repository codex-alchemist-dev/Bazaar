namespace Bazaar.Client.Tui.Dashboard.Tests;

public class KodoSeverityTests
{
    [Theory]
    [InlineData("failing", OpsSeverity.Serious)]
    [InlineData("Frozen", OpsSeverity.Serious)]
    [InlineData("degraded", OpsSeverity.Minor)]
    [InlineData("healthy", OpsSeverity.Ok)]
    [InlineData("recovering", OpsSeverity.Ok)]
    [InlineData("something-new", OpsSeverity.Minor)]
    [InlineData(null, OpsSeverity.Minor)]
    public void State_words_map_to_alert_grades(string? state, OpsSeverity expected) =>
        Assert.Equal(expected, KodoSeverity.FromState(state));

    [Theory]
    [InlineData("critical", OpsSeverity.Serious)]
    [InlineData("warning", OpsSeverity.Minor)]
    [InlineData("info", OpsSeverity.Ok)]
    public void Incident_severity_maps_the_same_way(string severity, OpsSeverity expected) =>
        Assert.Equal(expected, KodoSeverity.FromIncident(severity));
}

public class RestartDelayPolicyTests
{
    private static readonly DateTimeOffset _now = Samples.Now;

    [Fact]
    public void Delays_follow_the_defaults_and_stay_at_the_last_one()
    {
        var policy = new RestartDelayPolicy();
        var crashes = new List<DateTimeOffset>();
        var seen = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            crashes.Add(_now.AddMinutes(-10 + i));
            seen.Add(policy.DelayAfter(crashes, _now).TotalMinutes);
        }

        Assert.Equal([1d, 1d, 5d, 5d, 5d, 5d], seen);
    }

    [Fact]
    public void Old_crashes_no_longer_count_after_the_reset_window()
    {
        var policy = new RestartDelayPolicy();
        DateTimeOffset[] crashes = [_now.AddHours(-30), _now.AddHours(-26), _now.AddHours(-25), _now.AddMinutes(-1)];

        Assert.Equal(TimeSpan.FromMinutes(1), policy.DelayAfter(crashes, _now));
    }

    [Fact]
    public void Configured_delays_are_used_and_bad_entries_dropped()
    {
        var policy = new RestartDelayPolicy([0, 2, -4, 7], TimeSpan.FromHours(1));

        Assert.Equal([2, 7], policy.DelaysMinutes);
        Assert.Equal(TimeSpan.FromMinutes(7), policy.DelayAfter([_now.AddMinutes(-3), _now.AddMinutes(-2), _now], _now));
    }

    [Fact]
    public void Empty_or_invalid_settings_fall_back_to_the_defaults()
    {
        var policy = new RestartDelayPolicy([], TimeSpan.Zero);

        Assert.Equal(RestartDelayPolicy.DefaultDelaysMinutes, policy.DelaysMinutes);
        Assert.Equal(RestartDelayPolicy.DefaultFailureReset, policy.FailureReset);
    }

    [Fact]
    public void No_recorded_crash_gives_the_first_delay() =>
        Assert.Equal(TimeSpan.FromMinutes(1), new RestartDelayPolicy().DelayAfter([], _now));
}

public class AlertCenterTests
{
    private static AlertItem Item(string key) => new(key, key + " failed");

    [Fact]
    public void A_new_serious_failure_rings_once_and_shows_a_banner()
    {
        var alerts = new AlertCenter();

        Assert.True(alerts.Observe([Item("db")]));
        Assert.False(alerts.Observe([Item("db")]));
        Assert.Single(alerts.Banner);
    }

    [Fact]
    public void The_banner_stays_until_acknowledged_even_after_the_failure_clears()
    {
        var alerts = new AlertCenter();
        alerts.Observe([Item("db")]);
        alerts.Observe([]);

        Assert.Single(alerts.Banner);
        alerts.Acknowledge();
        Assert.Empty(alerts.Banner);
    }

    [Fact]
    public void An_acknowledged_failure_that_persists_does_not_come_back_or_ring()
    {
        var alerts = new AlertCenter();
        alerts.Observe([Item("db")]);
        alerts.Acknowledge();

        Assert.False(alerts.Observe([Item("db")]));
        Assert.Empty(alerts.Banner);
    }

    [Fact]
    public void A_failure_that_clears_and_recurs_is_new()
    {
        var alerts = new AlertCenter();
        alerts.Observe([Item("db")]);
        alerts.Acknowledge();
        alerts.Observe([]);

        Assert.True(alerts.Observe([Item("db")]));
        Assert.Single(alerts.Banner);
    }

    [Fact]
    public void Two_new_failures_in_one_poll_ring_once()
    {
        var alerts = new AlertCenter();

        Assert.True(alerts.Observe([Item("a"), Item("b")]));
        Assert.Equal(2, alerts.Banner.Count);
    }

    [Fact]
    public void The_bell_can_be_turned_off_and_the_banner_still_shows()
    {
        var alerts = new AlertCenter { BellEnabled = false };

        Assert.False(alerts.Observe([Item("db")]));
        Assert.Single(alerts.Banner);
    }

    [Fact]
    public void Only_major_wins_get_the_green_banner_until_dismissed()
    {
        var alerts = new AlertCenter();
        alerts.ObserveWins([Samples.Win("small"), Samples.Win("big", OpsEventKind.CrashSurvived, major: true)]);

        Assert.Equal("big", Assert.Single(alerts.WinBanner).Id);
        alerts.DismissWins();
        alerts.ObserveWins([Samples.Win("big", OpsEventKind.CrashSurvived, major: true)]);
        Assert.Empty(alerts.WinBanner);
    }

    [Fact]
    public void A_major_fault_is_not_a_win()
    {
        var alerts = new AlertCenter();
        alerts.ObserveWins([new OpsEvent("f", Samples.Now, OpsEventKind.Fault, "broke", Major: true)]);

        Assert.Empty(alerts.WinBanner);
    }
}
