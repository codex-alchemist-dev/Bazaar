using Bazaar.Client.Kodo;

namespace Bazaar.Client.Kodo.Tests.Kodo;

public class KodoStatusViewModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static KodoStatusSnapshot Snap(
        KodoHealthState state,
        KodoHealthState? underlying = null,
        int score = 90,
        params KodoControlState[] controls) =>
        new(state, underlying, score, _now, controls);

    [Theory]
    [InlineData(KodoHealthState.Healthy, "Healthy")]
    [InlineData(KodoHealthState.Degraded, "Degraded")]
    [InlineData(KodoHealthState.Failing, "Failing")]
    [InlineData(KodoHealthState.Recovering, "Recovering")]
    [InlineData(KodoHealthState.Frozen, "Frozen")]
    public void Every_state_has_a_word_and_keeps_its_state_for_the_theme(KodoHealthState state, string word)
    {
        var row = KodoStatusViewModel.Build(Snap(state), _now);

        Assert.Equal(word, row.StateWord);
        Assert.Equal(state, row.State);
    }

    [Fact]
    public void Frozen_shows_the_underlying_health_beside_it()
    {
        var row = KodoStatusViewModel.Build(Snap(KodoHealthState.Frozen, KodoHealthState.Failing), _now);

        Assert.Equal("Frozen, underlying failing", row.Headline);
    }

    [Fact]
    public void Score_is_shown_as_sent_and_clamped_to_the_scale()
    {
        Assert.Equal("Score 42 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Degraded, score: 42), _now).ScoreText);
        Assert.Equal("Score 0 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Failing, score: -5), _now).ScoreText);
        Assert.Equal("Score 100 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Healthy, score: 140), _now).ScoreText);
    }

    [Fact]
    public void A_recent_snapshot_is_not_stale()
    {
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddMinutes(-2), []), _now);

        Assert.False(row.IsStale);
        Assert.Equal("Updated 2 min ago", row.FreshnessText);
    }

    [Fact]
    public void An_old_snapshot_is_shown_as_offline_with_its_age()
    {
        var later = _now.AddHours(3).AddMinutes(10);
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddHours(3), []), later);

        Assert.True(row.IsStale);
        Assert.Equal("Offline: last seen 10 min ago", row.FreshnessText);
    }

    [Fact]
    public void A_snapshot_from_the_future_is_treated_as_now()
    {
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddMinutes(5), []), _now);

        Assert.False(row.IsStale);
        Assert.Equal("Updated under a minute ago", row.FreshnessText);
    }

    [Fact]
    public void Controls_are_shown_exactly_as_the_server_sent_them()
    {
        var row = KodoStatusViewModel.Build(
            Snap(KodoHealthState.Failing, controls: new KodoControlState(KodoControl.Restart, true, null)), _now);

        var control = Assert.Single(row.Controls);
        Assert.Equal(KodoControl.Restart, control.Control);
        Assert.Equal("Restart", control.Label);
        Assert.True(control.Enabled);
        Assert.Null(control.DisabledReason);
    }

    [Fact]
    public void A_disabled_control_carries_the_server_reason_unchanged()
    {
        var row = KodoStatusViewModel.Build(
            Snap(KodoHealthState.Frozen, KodoHealthState.Failing, controls:
            [
                new KodoControlState(KodoControl.Restart, false, "Incident mode is active; risky actions are blocked"),
                new KodoControlState(KodoControl.IncidentExit, true, null),
            ]),
            _now);

        Assert.Equal(["Restart", "Exit incident mode"], row.Controls.Select(c => c.Label));
        Assert.False(row.Controls[0].Enabled);
        Assert.Equal("Incident mode is active; risky actions are blocked", row.Controls[0].DisabledReason);
        Assert.True(row.Controls[1].Enabled);
    }

    [Fact]
    public void No_controls_from_the_server_means_no_controls_in_the_header()
    {
        var row = KodoStatusViewModel.Build(Snap(KodoHealthState.Failing), _now);

        Assert.Empty(row.Controls);
    }

    [Fact]
    public void Null_snapshot_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => KodoStatusViewModel.Build(null!, _now));
    }
}
