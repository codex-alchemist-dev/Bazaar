using Bazaar.Client.Core.Panes;

namespace Bazaar.Client.Core.Tests.Panes;

public class KanbanPaneViewModelTests
{
    [Fact]
    public void Always_has_five_columns_in_board_order_even_when_empty()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", "{\"tasks\":[]}"));

        Assert.Equal(["Backlog", "Ready", "In progress", "Review", "Done"], pane.Columns.Select(c => c.Name));
        Assert.All(pane.Columns, c => Assert.Empty(c.Cards));
        Assert.Equal(0, pane.SkippedTaskCount);
    }

    [Fact]
    public void Places_cards_by_state_ignoring_case_spaces_and_hyphens()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", """
            {"tasks":[
              {"id":"t1","title":"Schema","state":"Ready"},
              {"id":"t2","title":"Wire codec","state":"InProgress"},
              {"id":"t3","title":"Review pane","state":"review"},
              {"id":"t4","title":"Done thing","state":"done","blocked":false},
              {"id":"t5","title":"Stuck","state":"In-Progress","blocked":true}
            ]}
            """));

        Assert.Equal(["t1"], Ids(pane, "Ready"));
        Assert.Equal(["t2", "t5"], Ids(pane, "In progress"));
        Assert.Equal(["t3"], Ids(pane, "Review"));
        Assert.Equal(["t4"], Ids(pane, "Done"));
        Assert.Empty(Ids(pane, "Backlog"));
    }

    [Fact]
    public void Blocked_is_a_flag_on_the_card_not_a_column()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", """
            {"tasks":[{"id":"t1","title":"Waiting","state":"Ready","blocked":true}]}
            """));

        var card = pane.Columns.Single(c => c.Name == "Ready").Cards.Single();
        Assert.True(card.IsBlocked);
        Assert.Equal(5, pane.Columns.Count);
    }

    [Fact]
    public void Skips_tasks_with_unknown_state_or_missing_fields_and_counts_them()
    {
        var pane = new KanbanPaneViewModel(PaneRegistryTests.Descriptor("bazaar.kanban", """
            {"tasks":[
              {"id":"t1","title":"Good","state":"Backlog"},
              {"id":"t2","title":"Bad state","state":"Archived"},
              {"title":"No id","state":"Backlog"},
              "not an object"
            ]}
            """));

        Assert.Equal(["t1"], Ids(pane, "Backlog"));
        Assert.Equal(3, pane.SkippedTaskCount);
    }

    private static IEnumerable<string> Ids(KanbanPaneViewModel pane, string column) =>
        pane.Columns.Single(c => c.Name == column).Cards.Select(c => c.Id);
}
