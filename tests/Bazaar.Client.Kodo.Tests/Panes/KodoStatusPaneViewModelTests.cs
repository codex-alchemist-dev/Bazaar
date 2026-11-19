using Bazaar.Contracts.Kodo;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoStatusPaneViewModelTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Grants(params string[] granted) : IKodoPermissions
    {
        public bool Can(string permission) => granted.Contains(permission);
    }

    private static (KodoStatusPaneViewModel Model, KodoStreamReducer Reducer, FakeTimeProvider Time) Make(IKodoPermissions? permissions = null)
    {
        var time = new FakeTimeProvider(_start);
        var reducer = new KodoStreamReducer(time);
        return (new KodoStatusPaneViewModel(reducer, permissions ?? new KodoAllPermissions()), reducer, time);
    }

    [Theory]
    [InlineData("healthy", "Healthy")]
    [InlineData("degraded", "Degraded")]
    [InlineData("failing", "Failing")]
    [InlineData("recovering", "Recovering")]
    public void Each_state_shows_its_word_and_its_theme_token(string wire, string word)
    {
        var (model, reducer, _) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, wire));

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
        var (model, reducer, _) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, "frozen", "failing"));

        var panel = model.Build();

        Assert.Equal("Frozen, underlying failing", panel.Headline);
        Assert.True(panel.OpenIncident);
        Assert.Equal("Incident mode is open", panel.IncidentText);
    }

    [Fact]
    public void Silence_shows_unobserved_with_offline_age_last_known_score_and_no_controls()
    {
        var (model, reducer, time) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, controls: new KodoControlDto("restart", true, null)));
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
        var (model, reducer, time) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1));
        time.Advance(TimeSpan.FromSeconds(5));
        reducer.Apply(KodoStreamReducerTests.Beat(5));

        var panel = model.Build();

        Assert.Equal("Unobserved", panel.StateWord);
        Assert.EndsWith("waiting for a fresh snapshot", panel.OfflineText);
    }

    [Fact]
    public void Score_is_clamped_and_caps_and_alerts_are_called_out_when_reported()
    {
        var (model, reducer, _) = Make();
        reducer.Apply(new KodoStreamEventDto(1, KodoStreamEventDto.SnapshotType, _start, null,
            new KodoStatusSnapshotDto(1, 1, KodoStreamReducerTests.Status("failing", score: 140), null, [])));
        model.Extras = new KodoStatusExtras(2, [new KodoScoreCap("A critical probe is failing", 49)]);

        var panel = model.Build();

        Assert.Equal("Score 100 of 100", panel.ScoreText);
        Assert.Equal(["Capped at 49: A critical probe is failing"], panel.ScoreCapTexts);
        Assert.Equal("2 open alerts", panel.OpenAlertsText);
    }

    [Fact]
    public void Without_extras_alerts_are_not_reported_rather_than_zero()
    {
        var (model, reducer, _) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1));

        var panel = model.Build();

        Assert.Equal("Open alerts not reported", panel.OpenAlertsText);
        Assert.Empty(panel.ScoreCapTexts);
        Assert.Equal("No component data", panel.ComponentsText);
    }

    [Fact]
    public void One_alert_uses_the_singular()
    {
        var (model, reducer, _) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1));
        model.Extras = new KodoStatusExtras(1, []);

        Assert.Equal("1 open alert", model.Build().OpenAlertsText);
    }

    [Fact]
    public void Components_not_green_are_counted()
    {
        var (model, reducer, _) = Make();
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, components:
        [
            new KodoComponentDto("a", "green", null, _start),
            new KodoComponentDto("b", "red", "x", _start),
            new KodoComponentDto("c", "unknown", null, _start),
        ]));

        Assert.Equal("2 of 3 components not green", model.Build().ComponentsText);
    }

    [Fact]
    public void Controls_show_only_when_permitted_and_keep_the_server_flags()
    {
        var restart = new KodoControlDto("restart", false, "Blocked during incident");
        var isolate = new KodoControlDto("isolate", true, null);
        var (model, reducer, _) = Make(new Grants(KodoPermissions.ViewStatus, KodoPermissions.ControlRestart));
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, "frozen", "failing", null, restart, isolate));

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
        var (model, reducer, _) = Make(new KodoNoPermissions());
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, controls: new KodoControlDto("restart", true, null)));

        var panel = model.Build();

        Assert.False(panel.Permitted);
        Assert.Empty(panel.Controls);
    }

    [Fact]
    public void Requesting_a_control_raises_only_for_a_shown_enabled_control()
    {
        var (model, reducer, _) = Make(new Grants(KodoPermissions.ViewStatus, KodoPermissions.ControlRestart));
        reducer.Apply(KodoStreamReducerTests.Snapshot(1, controls: [new KodoControlDto("restart", true, null), new KodoControlDto("isolate", true, null)]));
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
