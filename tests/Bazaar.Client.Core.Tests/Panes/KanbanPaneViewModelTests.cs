using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class KanbanPaneViewModelTests
{
    private const string T1 = "0196a6f0-0000-7000-8000-000000000001";
    private const string T2 = "0196a6f0-0000-7000-8000-000000000002";
    private const string T3 = "0196a6f0-0000-7000-8000-000000000003";
    private const string T4 = "0196a6f0-0000-7000-8000-000000000004";
    private const string T5 = "0196a6f0-0000-7000-8000-000000000005";
    private const string Agent = "0196a6f0-0000-7000-8000-0000000000aa";

    [Fact]
    public void Always_has_five_columns_in_board_order_even_when_empty()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", "{\"cards\":[]}"));

        Assert.Equal(["Backlog", "Ready", "In progress", "Review", "Done"], pane.Columns.Select(c => c.Name));
        Assert.All(pane.Columns, c => Assert.Empty(c.Cards));
        Assert.Equal(0, pane.SkippedTaskCount);
    }

    [Fact]
    public void Places_cards_by_their_state_and_keeps_guid_ids()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", $$"""
            {"cards":[
              {"id":"{{T1}}","title":"Schema","state":"ready"},
              {"id":"{{T2}}","title":"Wire codec","state":"in_progress"},
              {"id":"{{T3}}","title":"Review pane","state":"review"},
              {"id":"{{T4}}","title":"Done thing","state":"done"},
              {"id":"{{T5}}","title":"Stuck","state":"in_progress"}
            ]}
            """));

        Assert.Equal([Guid.Parse(T1)], Ids(pane, "Ready"));
        // The board orders cards by title: "Stuck" before "Wire codec".
        Assert.Equal([Guid.Parse(T5), Guid.Parse(T2)], Ids(pane, "In progress"));
        Assert.Equal([Guid.Parse(T3)], Ids(pane, "Review"));
        Assert.Equal([Guid.Parse(T4)], Ids(pane, "Done"));
        Assert.Empty(Ids(pane, "Backlog"));
    }

    [Fact]
    public void A_card_with_unfinished_blockers_is_marked_blocked_and_a_card_without_them_is_not()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", $$"""
            {"cards":[
              {"id":"{{T1}}","title":"Waiting","state":"ready","unfinishedBlockerIds":["{{T2}}"],"blockerTitles":["Schema"]},
              {"id":"{{T2}}","title":"Free","state":"ready","unfinishedBlockerIds":[],"blockerTitles":[]}
            ]}
            """));

        var ready = pane.Columns.Single(c => c.Name == "Ready").Cards;
        Assert.True(ready.Single(c => c.Id == Guid.Parse(T1)).IsBlocked);
        Assert.False(ready.Single(c => c.Id == Guid.Parse(T2)).IsBlocked);
        Assert.Equal(5, pane.Columns.Count);
    }

    [Fact]
    public void A_done_blocker_does_not_block_its_dependent()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", $$"""
            {"cards":[{"id":"{{T1}}","title":"Next","state":"ready","unfinishedBlockerIds":[],"blockerTitles":[]}]}
            """));

        Assert.False(Assert.Single(pane.Columns.Single(c => c.Name == "Ready").Cards).IsBlocked);
    }

    [Fact]
    public void Skips_cards_with_bad_ids_bad_states_or_missing_fields_and_counts_them()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", $$"""
            {"cards":[
              {"id":"{{T1}}","title":"Good","state":"backlog"},
              {"id":"not-a-guid","title":"Bad id","state":"backlog"},
              {"id":"{{T2}}","title":"Bad state","state":"Archived"},
              {"id":"{{T3}}","title":"Bad state form","state":"In progress"},
              {"title":"No id","state":"backlog"},
              {"id":"{{T4}}","title":"Bad owner","state":"backlog","ownerAgentId":"nope"},
              "not an object"
            ]}
            """));

        Assert.Equal([Guid.Parse(T1)], Ids(pane, "Backlog"));
        Assert.Equal(6, pane.SkippedTaskCount);
    }

    [Fact]
    public void Owner_is_read_when_present()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", $$"""
            {"cards":[{"id":"{{T1}}","title":"Owned","state":"backlog","ownerAgentId":"{{Agent}}"}]}
            """));

        Assert.Equal([Guid.Parse(T1)], Ids(pane, "Backlog"));
        Assert.Equal(0, pane.SkippedTaskCount);
    }

    private static IEnumerable<Guid> Ids(KanbanPaneViewModel pane, string column) =>
        pane.Columns.Single(c => c.Name == column).Cards.Select(c => c.Id);
}
