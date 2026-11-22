using Bazaar.Client.Kodo.Live;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Client.Kodo.Tests.Panes.KodoTestFrames;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoStatusPaneViewModelTests
{
    private sealed class Grants(params string[] granted) : IKodoPermissions
    {
        public bool Can(string permission) => granted.Contains(permission);
    }

    private static (KodoStatusPaneViewModel Model, KodoStreamSession Session, FakeTimeProvider Time) Make(IKodoPermissions? permissions = null)
    {
        var time = new FakeTimeProvider(Start);
        var session = new KodoStreamSession(time);
        return (new KodoStatusPaneViewModel(session, permissions ?? new KodoAllPermissions()), session, time);
    }

    [Theory]
    [InlineData("healthy", "Healthy")]
    [InlineData("degraded", "Degraded")]
    [InlineData("failing", "Failing")]
    [InlineData("recovering", "Recovering")]
    public void Each_state_shows_its_word_and_its_theme_token(string wire, string word)
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1, wire));

        var panel = model.Build();

        Assert.Equal(word, panel.StateWord);
        Assert.Equal(word, panel.Headline);
        Assert.Equal("Bazaar.brush.kodo.state." + wire, panel.ColourToken);
        Assert.False(panel.IsOffline);
        Assert.Contains(word, panel.AccessibleSummary);
    }

    [Fact]
    public void Frozen_shows_the_underlying_state_and_an_open_incident()
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1, "frozen", "failing"));

        var panel = model.Build();

        Assert.Equal("Frozen, underlying failing", panel.Headline);
        Assert.True(panel.OpenIncident);
        Assert.Equal("Incident mode is open", panel.IncidentText);
    }

    [Fact]
    public void Silence_shows_unobserved_with_offline_age_last_known_score_and_no_controls()
    {
        var (model, session, time) = Make();
        session.Apply(Snapshot(1));
        model.Controls = [Control(KodoControl.Restart)];
        time.Advance(TimeSpan.FromMinutes(3));

        var panel = model.Build();

        Assert.True(panel.IsOffline);
        Assert.Equal("Unobserved", panel.StateWord);
        Assert.Equal("Bazaar.brush.kodo.state.unobserved", panel.ColourToken);
        Assert.Equal("Offline: last event 3 min ago", panel.OfflineText);
        Assert.Equal("Last known score 90 of 100", panel.ScoreText);
        Assert.Empty(panel.Controls);
        Assert.False(panel.OpenIncident);
    }

    [Fact]
    public void Before_any_data_the_pane_says_no_event_received()
    {
        var (model, _, _) = Make();

        var panel = model.Build();

        Assert.Equal("Unobserved", panel.StateWord);
        Assert.Equal("Offline: no event received yet", panel.OfflineText);
        Assert.Equal("Score not available", panel.ScoreText);
    }

    [Fact]
    public void A_gap_shows_unobserved_and_waiting_for_a_snapshot()
    {
        var (model, session, time) = Make();
        session.Apply(Snapshot(1));
        time.Advance(TimeSpan.FromSeconds(5));
        session.Apply(Beat(5));

        var panel = model.Build();

        Assert.Equal("Unobserved", panel.StateWord);
        Assert.EndsWith("waiting for a fresh snapshot", panel.OfflineText);
    }

    [Fact]
    public void Score_is_clamped_and_caps_and_alerts_are_called_out_when_reported()
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1, "failing", score: 140));
        model.Extras = new KodoStatusExtras(2, [new KodoScoreCap("A critical probe is failing", 49)]);

        var panel = model.Build();

        Assert.Equal("Score 100 of 100", panel.ScoreText);
        Assert.Equal(["Capped at 49: A critical probe is failing"], panel.ScoreCapTexts);
        Assert.Equal("2 open alerts", panel.OpenAlertsText);
    }

    [Fact]
    public void Without_extras_alerts_are_not_reported_rather_than_zero()
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1));

        var panel = model.Build();

        Assert.Equal("Open alerts not reported", panel.OpenAlertsText);
        Assert.Empty(panel.ScoreCapTexts);
        Assert.Equal("No component data", panel.ComponentsText);
    }

    [Fact]
    public void One_alert_uses_the_singular()
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1));
        model.Extras = new KodoStatusExtras(1, []);

        Assert.Equal("1 open alert", model.Build().OpenAlertsText);
    }

    [Fact]
    public void Components_not_green_are_counted()
    {
        var (model, session, _) = Make();
        session.Apply(Snapshot(1, components:
        [
            new KodoComponentWire("a", "green", null, Start),
            new KodoComponentWire("b", "red", "x", Start),
            new KodoComponentWire("c", "unknown", null, Start),
        ]));

        Assert.Equal("2 of 3 components not green", model.Build().ComponentsText);
    }

    [Fact]
    public void Controls_show_only_when_permitted_and_keep_the_server_flags()
    {
        var restart = Control(KodoControl.Restart, false, "Blocked during incident");
        var isolate = Control(KodoControl.Isolate);
        var (model, session, _) = Make(new Grants(KodoPermissions.ViewStatus, KodoPermissions.ControlRestart));
        session.Apply(Snapshot(1, "frozen", "failing"));
        model.Controls = [restart, isolate];

        var panel = model.Build();

        var row = Assert.Single(panel.Controls);
        Assert.Equal(KodoControl.Restart, row.Control);
        Assert.False(row.Enabled);
        Assert.Equal("Blocked during incident", row.DisabledReason);
        Assert.Equal("Restart, unavailable: Blocked during incident", row.AccessibleName);
    }

    [Fact]
    public void A_viewer_with_no_role_sees_no_status_and_no_controls()
    {
        var (model, session, _) = Make(new KodoNoPermissions());
        session.Apply(Snapshot(1));
        model.Controls = [Control(KodoControl.Restart)];

        var panel = model.Build();

        Assert.False(panel.Permitted);
        Assert.Empty(panel.Controls);
    }

    [Fact]
    public void Requesting_a_control_raises_only_for_a_shown_enabled_control()
    {
        var (model, session, _) = Make(new Grants(KodoPermissions.ViewStatus, KodoPermissions.ControlRestart));
        session.Apply(Snapshot(1));
        model.Controls = [Control(KodoControl.Restart), Control(KodoControl.Isolate)];
        var raised = new List<KodoControl>();
        model.ControlRequested += (_, c) => raised.Add(c);

        Assert.True(model.RequestControl(KodoControl.Restart));
        Assert.False(model.RequestControl(KodoControl.Isolate));
        Assert.False(model.RequestControl(KodoControl.RollBack));

        Assert.Equal([KodoControl.Restart], raised);
    }

    [Fact]
    public void Mnemonic_rows_are_proposed_and_unique()
    {
        var all = KodoMnemonics.Proposed;

        Assert.Equal(["kodo", "kstat", "kfleet"], all.Select(m => m.Mnemonic));
        Assert.Equal(all.Count, all.Select(m => m.CommandId).Distinct().Count());
    }
}
