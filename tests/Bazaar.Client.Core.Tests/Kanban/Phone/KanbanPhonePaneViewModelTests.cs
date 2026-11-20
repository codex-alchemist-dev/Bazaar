using Bazaar.Client.Core.Panes;
using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Client.RyuseiFuda.Workday;
using Bazaar.Contracts.Panes;
using static Bazaar.Client.Core.Tests.Kanban.Phone.PhoneCards;

namespace Bazaar.Client.Core.Tests.Kanban.Phone;

public class KanbanPhonePaneViewModelTests
{
    private static readonly string[] _columnTitles = ["Backlog", "Ready", "In progress", "Review", "Done"];

    [Fact]
    public void Pane_identity_is_the_kanban_kind_with_the_board_heading()
    {
        var pane = new KanbanPhonePaneViewModel(new FakeKanbanBoardClient(), ProjectId);

        Assert.Equal(PaneKinds.Kanban, pane.Kind);
        Assert.Equal("RyuseiFuda · Board", pane.Title);
    }

    [Fact]
    public async Task Load_shows_all_five_columns_in_board_order_even_when_empty()
    {
        var pane = new KanbanPhonePaneViewModel(new FakeKanbanBoardClient(), ProjectId);

        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_columnTitles, pane.Columns.Select(column => column.Title));
        Assert.All(pane.Columns, column => Assert.Empty(column.Cards));
        Assert.Null(pane.Status);
    }

    [Fact]
    public async Task Cards_sit_in_their_column_ordered_by_title_then_id()
    {
        var client = new FakeKanbanBoardClient(
            Card(3, "Beta", "ready"),
            Card(1, "Alpha", "ready"),
            Card(2, "Zeta", "backlog"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);

        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Id(2)], pane.Columns[0].Cards.Select(card => card.Id));
        Assert.Equal([Id(1), Id(3)], pane.Columns[1].Cards.Select(card => card.Id));
    }

    [Fact]
    public async Task Blocked_is_derived_from_unfinished_blockers_and_is_not_a_column()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Dependent", "ready", 1, "Schema"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);

        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(pane.Columns[1].Cards);
        Assert.True(card.Blocked);
        Assert.Equal(["Schema"], card.BlockerTitles);
        Assert.Equal(5, pane.Columns.Count);
    }

    [Fact]
    public async Task A_move_sends_the_revision_the_card_was_last_read_at()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 7));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        await pane.MoveAsync(Id(1), KanbanStates.InProgress, TestContext.Current.CancellationToken);

        var call = Assert.Single(client.Moves);
        Assert.Equal(new MoveCall(Id(1), KanbanStates.InProgress, 7), call);
    }

    [Fact]
    public async Task An_applied_move_reloads_the_board_so_dependents_follow()
    {
        var client = new FakeKanbanBoardClient(
            Card(1, "Schema", "ready", revision: 1),
            Card(2, "Migration", "backlog", revision: 1, "Schema"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var loadsBefore = client.LoadCalls;

        var result = await pane.MoveAsync(Id(1), KanbanStates.InProgress, TestContext.Current.CancellationToken);

        Assert.Equal(new KanbanMoveResult.Applied(2), result);
        Assert.Equal("Moved to In progress.", pane.Status);
        Assert.Equal(loadsBefore + 1, client.LoadCalls);
        Assert.Equal([Id(1)], pane.Columns[2].Cards.Select(card => card.Id));
        Assert.Equal(2, pane.Columns[2].Cards[0].Revision);
    }

    [Fact]
    public async Task A_stale_revision_is_a_conflict_that_is_not_overwritten_and_reloads_the_board()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 7));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        // Another writer moved the card to Review and bumped its revision after the phone last read it.
        client.ChangeOnServer(Card(1, "Schema", "review", revision: 8));

        var result = await pane.MoveAsync(Id(1), KanbanStates.Done, TestContext.Current.CancellationToken);

        Assert.Equal(new KanbanMoveResult.Conflict(8), result);
        // Sent once: the pane does not retry with the new revision on its own.
        Assert.Single(client.Moves);
        // The server's copy is untouched by the stale move, and the pane now shows it.
        Assert.Equal("review", client.ServerCard(Id(1)).State);
        Assert.Equal(8, client.ServerCard(Id(1)).Revision);
        Assert.Equal([Id(1)], pane.Columns[3].Cards.Select(card => card.Id));
        Assert.Equal(8, pane.Columns[3].Cards[0].Revision);
        Assert.Empty(pane.Columns[4].Cards);
        Assert.Contains("changed on the server", pane.Status);
        Assert.Contains("reloaded", pane.Status);
        Assert.False(pane.IsBusy);
    }

    [Fact]
    public async Task A_refused_move_leaves_the_card_where_it_was_and_names_the_blockers()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Dependent", "ready", revision: 3, "Schema", "Auth"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var result = await pane.MoveAsync(Id(1), KanbanStates.InProgress, TestContext.Current.CancellationToken);

        var refused = Assert.IsType<KanbanMoveResult.Refused>(result);
        Assert.Equal(["Schema", "Auth"], refused.BlockerTitles);
        Assert.Equal("Finish the prerequisites first. Blocked by Schema, Auth.", pane.Status);
        Assert.Equal([Id(1)], pane.Columns[1].Cards.Select(card => card.Id));
        Assert.Equal(3, client.ServerCard(Id(1)).Revision);
    }

    [Fact]
    public async Task An_invalid_result_from_the_service_is_shown_and_changes_nothing()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 2))
        {
            ForcedResult = new KanbanMoveResult.Invalid("Moves need a reason."),
        };
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        await pane.MoveAsync(Id(1), KanbanStates.Review, TestContext.Current.CancellationToken);

        Assert.Equal("Moves need a reason.", pane.Status);
        Assert.Equal([Id(1)], pane.Columns[1].Cards.Select(card => card.Id));
    }

    [Fact]
    public async Task Moving_a_card_to_the_column_it_is_already_in_sends_nothing()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var result = await pane.MoveAsync(Id(1), KanbanStates.Ready, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task A_card_that_is_not_on_the_board_is_reported_and_nothing_is_sent()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var result = await pane.MoveAsync(Id(9), KanbanStates.Done, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(client.Moves);
        Assert.Equal("That card is no longer on the board.", pane.Status);
    }

    [Fact]
    public async Task A_target_outside_the_five_stored_states_is_an_argument_error()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => pane.MoveAsync(Id(1), "archived", TestContext.Current.CancellationToken));
        Assert.Empty(client.Moves);
    }

    [Fact]
    public async Task A_second_move_while_one_is_in_flight_is_ignored()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready", revision: 1), Card(2, "Auth", "ready", revision: 1))
        {
            HeldMove = new TaskCompletionSource<KanbanMoveResult>(),
        };
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var first = pane.MoveAsync(Id(1), KanbanStates.InProgress, TestContext.Current.CancellationToken);
        Assert.True(pane.IsBusy);

        var second = await pane.MoveAsync(Id(2), KanbanStates.InProgress, TestContext.Current.CancellationToken);
        Assert.Null(second);
        Assert.Single(client.Moves);

        client.HeldMove.SetResult(new KanbanMoveResult.Applied(2));
        await first;
        Assert.False(pane.IsBusy);
    }

    [Fact]
    public async Task A_failed_load_keeps_the_last_board_and_says_so()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        client.FailLoads = true;
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Could not load the board.", pane.Status);
        Assert.Equal([Id(1)], pane.Columns[1].Cards.Select(card => card.Id));
    }

    [Fact]
    public async Task A_card_in_a_state_the_phone_does_not_know_fails_the_load_instead_of_vanishing()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "archived"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);

        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Could not load the board.", pane.Status);
        Assert.All(pane.Columns, column => Assert.Empty(column.Cards));
    }

    [Fact]
    public async Task Changed_is_raised_for_the_load_and_for_the_move()
    {
        var client = new FakeKanbanBoardClient(Card(1, "Schema", "ready"));
        var pane = new KanbanPhonePaneViewModel(client, ProjectId);
        var raised = 0;
        pane.Changed += (_, _) => raised++;

        await pane.LoadAsync(TestContext.Current.CancellationToken);
        var afterLoad = raised;
        await pane.MoveAsync(Id(1), KanbanStates.Done, TestContext.Current.CancellationToken);

        Assert.True(afterLoad > 0);
        Assert.True(raised > afterLoad);
    }
}
