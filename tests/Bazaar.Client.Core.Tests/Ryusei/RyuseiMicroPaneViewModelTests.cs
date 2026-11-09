using Bazaar.Client.Core.Ryusei;

namespace Bazaar.Client.Core.Tests.Ryusei;

public class RyuseiMicroPaneViewModelTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private static RyuseiTriggerSummary Trigger(
        string id,
        string state = "active",
        DateTimeOffset? next = null,
        params string[] targets) =>
        new(id, "timer", state, next, targets, $"purpose {id}");

    [Fact]
    public void Upcoming_triggers_come_first_soonest_first()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("c", next: _t0.AddHours(3)),
            Trigger("a", next: _t0.AddHours(1)),
            Trigger("b", next: _t0.AddHours(2)),
        ]);

        Assert.Equal(["a", "b", "c"], pane.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Ties_on_next_fire_break_on_id()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("z", next: _t0),
            Trigger("m", next: _t0),
        ]);

        Assert.Equal(["m", "z"], pane.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Paused_rows_sort_after_upcoming_rows()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("paused-early", "paused", _t0.AddHours(1)),
            Trigger("upcoming-late", "active", _t0.AddHours(5)),
            Trigger("upcoming-early", "active", _t0.AddHours(2)),
        ]);

        Assert.Equal(["upcoming-early", "upcoming-late", "paused-early"], pane.Rows.Select(r => r.Id));
        Assert.Equal("Paused", pane.Rows[2].StateLabel);
    }

    [Fact]
    public void Paused_rows_sort_soonest_first_and_those_without_a_next_fire_last()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("p-none", "paused", null),
            Trigger("p-late", "paused", _t0.AddHours(4)),
            Trigger("p-early", "paused", _t0.AddHours(1)),
        ]);

        Assert.Equal(["p-early", "p-late", "p-none"], pane.Rows.Select(r => r.Id));
    }

    [Fact]
    public void Other_states_and_active_without_a_next_fire_come_last_in_id_order()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("fired-1", "fired", _t0.AddHours(-1)),
            Trigger("active-none", "active", null),
            Trigger("cancelled-1", "cancelled", null),
            Trigger("up", "active", _t0),
        ]);

        Assert.Equal(["up", "active-none", "cancelled-1", "fired-1"], pane.Rows.Select(r => r.Id));
    }

    [Fact]
    public void A_null_next_fire_shows_a_plain_label_and_does_not_throw()
    {
        var pane = new RyuseiMicroPaneViewModel([Trigger("a", "active", null)]);

        var row = Assert.Single(pane.Rows);
        Assert.Null(row.NextFire);
        Assert.Equal("No next fire", row.NextFireText);
    }

    [Fact]
    public void A_finished_trigger_shows_no_next_fire_even_if_the_server_sent_a_time()
    {
        var pane = new RyuseiMicroPaneViewModel([Trigger("a", "cancelled", _t0)]);

        Assert.Equal("No next fire", Assert.Single(pane.Rows).NextFireText);
    }

    [Fact]
    public void A_fire_time_is_shown_with_the_offset_the_server_sent()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            Trigger("a", "active", new DateTimeOffset(2026, 11, 9, 14, 30, 0, TimeSpan.FromHours(2))),
        ]);

        Assert.Equal("Next 2026-11-09 14:30 +02:00", Assert.Single(pane.Rows).NextFireText);
    }

    [Fact]
    public void Target_names_are_joined_in_the_order_given()
    {
        var pane = new RyuseiMicroPaneViewModel([Trigger("a", "active", _t0, "Charles", "Mira", "Tobin")]);

        Assert.Equal("Charles, Mira, Tobin", Assert.Single(pane.Rows).TargetsText);
    }

    [Fact]
    public void An_empty_target_list_reads_as_no_targets()
    {
        var pane = new RyuseiMicroPaneViewModel([Trigger("a", "active", _t0)]);

        Assert.Equal("No targets", Assert.Single(pane.Rows).TargetsText);
    }

    [Fact]
    public void Blank_target_names_are_dropped()
    {
        var pane = new RyuseiMicroPaneViewModel([Trigger("a", "active", _t0, "Charles", " ", "", "Mira")]);

        Assert.Equal("Charles, Mira", Assert.Single(pane.Rows).TargetsText);
        var onlyBlank = new RyuseiMicroPaneViewModel([Trigger("b", "active", _t0, " ")]);
        Assert.Equal("No targets", Assert.Single(onlyBlank.Rows).TargetsText);
    }

    [Fact]
    public void Kind_and_state_labels_are_plain_english_and_unknown_values_do_not_throw()
    {
        var pane = new RyuseiMicroPaneViewModel(
        [
            new RyuseiTriggerSummary("1", "interval", "active", _t0, [], "p"),
            new RyuseiTriggerSummary("2", "watcher", "paused", null, [], "p"),
            new RyuseiTriggerSummary("3", "event", "expired", null, [], "p"),
            new RyuseiTriggerSummary("4", "cron", "weird", null, [], "p"),
        ]);

        Assert.Equal("Repeating interval", pane.Rows[0].KindLabel);
        Assert.Equal("Condition watcher", pane.Rows[1].KindLabel);
        Assert.Equal("One-shot event", pane.Rows[2].KindLabel);
        Assert.Equal("Unknown trigger type", pane.Rows[3].KindLabel);
        Assert.Equal("Unknown state", pane.Rows[3].StateLabel);
        Assert.Equal("Expired", pane.Rows[2].StateLabel);
    }

    [Fact]
    public void Purpose_is_carried_through_unchanged()
    {
        var pane = new RyuseiMicroPaneViewModel([new RyuseiTriggerSummary("a", "timer", "active", _t0, ["Charles"], "Check the build")]);

        Assert.Equal("Check the build", Assert.Single(pane.Rows).Purpose);
    }

    [Fact]
    public void No_triggers_gives_no_rows()
    {
        var pane = new RyuseiMicroPaneViewModel([]);

        Assert.Empty(pane.Rows);
        Assert.Equal("RyuseiFuda · Schedule", RyuseiMicroPaneViewModel.Title);
    }
}
