using System.Globalization;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Panes;
using Bazaar.Contracts.Ryusei;

namespace Bazaar.Client.Core.Tests.Panes;

public sealed class RyuseiTriggerJoinTests
{
    private static readonly Guid _taskA = Guid.Parse("0196a6f0-0000-7000-8000-00000000000a");
    private static readonly Guid _taskB = Guid.Parse("0196a6f0-0000-7000-8000-00000000000b");

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static RyuseiTriggerChip Chip(string id, Guid? task, string kind, string state, DateTimeOffset? next, string purpose) =>
        new(Guid.Parse(id), task, kind, state, next, purpose, 1);

    [Fact]
    public void ByTask_groups_live_chips_under_their_task_and_drops_triggers_with_no_task()
    {
        var triggers = new[]
        {
            Chip("00000000-0000-0000-0000-000000000001", _taskA, "timer", "active", At("2026-11-13T09:00:00+00:00"), "Check review"),
            Chip("00000000-0000-0000-0000-000000000002", _taskA, "event", "fired", null, "Already ran"),
            Chip("00000000-0000-0000-0000-000000000003", _taskB, "interval", "paused", null, "Weekly sweep"),
            Chip("00000000-0000-0000-0000-000000000004", null, "timer", "active", At("2026-11-13T08:00:00+00:00"), "No card"),
        };

        var byTask = RyuseiChipRules.ByTask(triggers);

        Assert.Equal(2, byTask.Count);
        // Fired is terminal and is dropped by Visible; the chip keeps the trigger id as its id.
        var a = Assert.Single(byTask[_taskA]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", a.TriggerId);
        Assert.Equal("Check review", a.Purpose);
        Assert.Equal(At("2026-11-13T09:00:00+00:00"), a.NextFire);
        Assert.False(byTask.ContainsKey(Guid.Empty));
        Assert.Equal(["00000000-0000-0000-0000-000000000003"], byTask[_taskB].Select(c => c.TriggerId));
    }

    [Fact]
    public void A_card_takes_its_joined_chips_and_the_snapshot_array_is_ignored_when_a_join_is_given()
    {
        const string board = """
            {"cards":[
              {"id":"0196a6f0-0000-7000-8000-00000000000a","title":"Schema","state":"ready",
               "triggers":[{"id":"from-snapshot","kind":"timer","state":"active","nextFire":null,"purpose":"Stale"}]},
              {"id":"0196a6f0-0000-7000-8000-00000000000b","title":"Plain","state":"backlog"}
            ]}
            """;
        var joined = RyuseiChipRules.ByTask([
            Chip("00000000-0000-0000-0000-000000000001", _taskA, "timer", "active", At("2026-11-13T09:00:00+00:00"), "Check review"),
        ]);

        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Kanban, board), joined);

        Assert.Equal(["00000000-0000-0000-0000-000000000001"], pane.ChipsFor(_taskA).Select(c => c.TriggerId));
        Assert.Empty(pane.ChipsFor(_taskB));
    }

    [Fact]
    public void Without_a_join_the_pane_still_reads_the_snapshot_array()
    {
        const string board = """
            {"cards":[
              {"id":"0196a6f0-0000-7000-8000-00000000000a","title":"Schema","state":"ready",
               "triggers":[{"id":"t1","kind":"timer","state":"active","nextFire":null,"purpose":"From snapshot"}]}
            ]}
            """;

        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor(PaneKinds.Kanban, board));

        Assert.Equal(["t1"], pane.ChipsFor(_taskA).Select(c => c.TriggerId));
    }
}
