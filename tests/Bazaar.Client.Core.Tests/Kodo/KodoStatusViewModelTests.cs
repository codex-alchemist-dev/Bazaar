using Bazaar.Client.Core.Kodo;

namespace Bazaar.Client.Core.Tests.Kodo;

public class KodoStatusViewModelTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static KodoStatusSnapshot Snap(KodoHealthState state, KodoHealthState? underlying = null, int score = 90) =>
        new(state, underlying, score, _now);

    [Theory]
    [InlineData(KodoHealthState.Healthy, "Healthy", KodoColourToken.Green)]
    [InlineData(KodoHealthState.Degraded, "Degraded", KodoColourToken.Amber)]
    [InlineData(KodoHealthState.Failing, "Failing", KodoColourToken.Red)]
    [InlineData(KodoHealthState.Recovering, "Recovering", KodoColourToken.Amber)]
    [InlineData(KodoHealthState.Frozen, "Frozen", KodoColourToken.Incident)]
    public void Every_state_has_a_word_and_a_colour(KodoHealthState state, string word, KodoColourToken colour)
    {
        var row = KodoStatusViewModel.Build(Snap(state), _now, new HashSet<string>());

        Assert.Equal(word, row.StateWord);
        Assert.Equal(colour, row.Colour);
    }

    [Fact]
    public void Frozen_shows_the_underlying_health_beside_it()
    {
        var row = KodoStatusViewModel.Build(
            Snap(KodoHealthState.Frozen, KodoHealthState.Failing), _now, new HashSet<string>());

        Assert.Equal("Frozen, underlying failing", row.Headline);
        Assert.Equal(KodoColourToken.Incident, row.Colour);
    }

    [Fact]
    public void Score_is_shown_as_sent_and_clamped_to_the_scale()
    {
        Assert.Equal("Score 42 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Degraded, score: 42), _now, new HashSet<string>()).ScoreText);
        Assert.Equal("Score 0 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Failing, score: -5), _now, new HashSet<string>()).ScoreText);
        Assert.Equal("Score 100 of 100", KodoStatusViewModel.Build(Snap(KodoHealthState.Healthy, score: 140), _now, new HashSet<string>()).ScoreText);
    }

    [Fact]
    public void A_recent_snapshot_is_not_stale()
    {
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddMinutes(-2)), _now, new HashSet<string>());

        Assert.False(row.IsStale);
        Assert.Equal("Updated 2 min ago", row.FreshnessText);
    }

    [Fact]
    public void An_old_snapshot_is_shown_as_offline_with_its_age()
    {
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddHours(3)), _now.AddHours(3).AddMinutes(10), new HashSet<string>());

        Assert.True(row.IsStale);
        Assert.Equal("Offline: last seen 10 min ago", row.FreshnessText);
    }

    [Fact]
    public void A_snapshot_from_the_future_is_treated_as_now()
    {
        var row = KodoStatusViewModel.Build(
            new KodoStatusSnapshot(KodoHealthState.Healthy, null, 90, _now.AddMinutes(5)), _now, new HashSet<string>());

        Assert.False(row.IsStale);
        Assert.Equal("Updated under a minute ago", row.FreshnessText);
    }

    [Fact]
    public void Controls_show_only_for_granted_roles()
    {
        var row = KodoStatusViewModel.Build(
            Snap(KodoHealthState.Failing), _now, new HashSet<string> { KodoStatusViewModel.RestartGrant });

        Assert.Equal([KodoControl.Restart], row.Controls.Select(c => c.Control));
        Assert.True(row.Controls[0].Enabled);
        Assert.Null(row.Controls[0].DisabledReason);
    }

    [Fact]
    public void No_grants_means_no_controls()
    {
        var row = KodoStatusViewModel.Build(Snap(KodoHealthState.Failing), _now, new HashSet<string>());

        Assert.Empty(row.Controls);
    }

    [Fact]
    public void While_frozen_only_incident_exit_is_enabled()
    {
        var all = new HashSet<string>
        {
            KodoStatusViewModel.RestartGrant,
            KodoStatusViewModel.IsolateGrant,
            KodoStatusViewModel.RollBackGrant,
            KodoStatusViewModel.IncidentExitGrant,
        };

        var row = KodoStatusViewModel.Build(Snap(KodoHealthState.Frozen, KodoHealthState.Failing), _now, all);

        Assert.Equal(4, row.Controls.Count);
        foreach (var control in row.Controls)
        {
            if (control.Control == KodoControl.IncidentExit)
            {
                Assert.True(control.Enabled);
            }
            else
            {
                Assert.False(control.Enabled);
                Assert.Equal("Incident mode is active; risky actions are blocked", control.DisabledReason);
            }
        }
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => KodoStatusViewModel.Build(null!, _now, new HashSet<string>()));
        Assert.Throws<ArgumentNullException>(() => KodoStatusViewModel.Build(Snap(KodoHealthState.Healthy), _now, null!));
    }
}
