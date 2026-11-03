using Bazaar.Client.Core.Kanban;

namespace Bazaar.Client.Core.Tests.Kanban;

public class KanbanBoardViewModelTests
{
    private static readonly Guid _cardA = Guid.Parse("0196a6f0-0000-7000-8000-000000000101");
    private static readonly Guid _cardB = Guid.Parse("0196a6f0-0000-7000-8000-000000000102");
    private static readonly Guid _cardC = Guid.Parse("0196a6f0-0000-7000-8000-000000000103");
    private static readonly Guid _cardD = Guid.Parse("0196a6f0-0000-7000-8000-000000000104");
    private static readonly Guid _owner = Guid.Parse("0196a6f0-0000-7000-8000-000000000200");

    private static KanbanCard Card(
        Guid id,
        string title,
        string state,
        IReadOnlyList<Guid>? blockers = null,
        IReadOnlyList<string>? blockerTitles = null) => new(
        id, title, state, _owner, blockers ?? [], blockerTitles ?? []);

    [Fact]
    public void Board_has_five_columns_in_order()
    {
        var board = new KanbanBoardViewModel([]);

        Assert.Equal(
            ["Backlog", "Ready", "In progress", "Review", "Done"],
            board.Columns.Select(column => column.Title));
        Assert.Equal(
            ["backlog", "ready", "in_progress", "review", "done"],
            board.Columns.Select(column => column.State));
        Assert.All(board.Columns, column => Assert.Empty(column.Cards));
    }

    [Fact]
    public void Cards_land_in_the_column_of_their_state()
    {
        var board = new KanbanBoardViewModel(
        [
            Card(_cardA, "Write spec", "backlog"),
            Card(_cardB, "Build parser", "in_progress"),
            Card(_cardC, "Ship", "done"),
        ]);

        Assert.Equal([_cardA], board.Columns[0].Cards.Select(card => card.Id));
        Assert.Empty(board.Columns[1].Cards);
        Assert.Equal([_cardB], board.Columns[2].Cards.Select(card => card.Id));
        Assert.Empty(board.Columns[3].Cards);
        Assert.Equal([_cardC], board.Columns[4].Cards.Select(card => card.Id));
    }

    [Fact]
    public void Card_with_unfinished_blocker_is_blocked_and_stays_in_its_column()
    {
        var board = new KanbanBoardViewModel(
        [
            Card(_cardA, "Dependent", "ready", [_cardB], ["Base"]),
        ]);

        var card = Assert.Single(board.Columns[1].Cards);
        Assert.True(card.Blocked);
        Assert.Equal(["Base"], card.BlockerChips);
    }

    [Fact]
    public void Card_without_blockers_is_not_blocked()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "Free", "ready")]);

        var card = Assert.Single(board.Columns[1].Cards);
        Assert.False(card.Blocked);
        Assert.Empty(card.BlockerChips);
    }

    [Fact]
    public void Blocked_is_never_a_column()
    {
        var board = new KanbanBoardViewModel(
        [
            Card(_cardA, "Dependent", "in_progress", [_cardB], ["Base"]),
        ]);

        Assert.DoesNotContain(board.Columns, column => column.Title.Contains("Blocked", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(board.Columns, column => column.State == "blocked");
        Assert.Equal([_cardA], board.Columns[2].Cards.Select(card => card.Id));
    }

    [Fact]
    public void Cards_are_ordered_by_title_then_id()
    {
        var board = new KanbanBoardViewModel(
        [
            Card(_cardC, "Same", "ready"),
            Card(_cardA, "Zebra", "ready"),
            Card(_cardB, "Same", "ready"),
            Card(_cardD, "Apple", "ready"),
        ]);

        Assert.Equal([_cardD, _cardB, _cardC, _cardA], board.Columns[1].Cards.Select(card => card.Id));
    }

    [Fact]
    public void Ordering_does_not_depend_on_input_order()
    {
        var forward = new KanbanBoardViewModel([Card(_cardA, "A", "review"), Card(_cardB, "B", "review")]);
        var reverse = new KanbanBoardViewModel([Card(_cardB, "B", "review"), Card(_cardA, "A", "review")]);

        Assert.Equal(
            forward.Columns[3].Cards.Select(card => card.Id),
            reverse.Columns[3].Cards.Select(card => card.Id));
    }

    [Fact]
    public void Refuse_sets_the_refusal_message_and_raises_changed()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "Dependent", "ready", [_cardB], ["Base"])]);
        var raised = 0;
        board.Changed += (_, _) => raised++;

        board.Refuse(_cardA, "Blocked by Base: finish it first.");

        Assert.Equal("Blocked by Base: finish it first.", board.RefusalMessage);
        Assert.NotNull(board.LastRefusal);
        Assert.Equal(_cardA, board.LastRefusal.CardId);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Refuse_replaces_an_earlier_refusal()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "A", "ready"), Card(_cardB, "B", "ready")]);

        board.Refuse(_cardA, "first");
        board.Refuse(_cardB, "second");

        Assert.Equal("second", board.RefusalMessage);
    }

    [Fact]
    public void ClearRefusal_removes_the_message_and_raises_changed()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "A", "ready")]);
        board.Refuse(_cardA, "no");
        var raised = 0;
        board.Changed += (_, _) => raised++;

        board.ClearRefusal();

        Assert.Null(board.RefusalMessage);
        Assert.Null(board.LastRefusal);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ClearRefusal_without_a_refusal_raises_nothing()
    {
        var board = new KanbanBoardViewModel([]);
        var raised = 0;
        board.Changed += (_, _) => raised++;

        board.ClearRefusal();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Refuse_rejects_an_empty_reason()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "A", "ready")]);

        Assert.Throws<ArgumentException>(() => board.Refuse(_cardA, "  "));
        Assert.Null(board.RefusalMessage);
    }

    [Fact]
    public void Refuse_rejects_a_card_that_is_not_on_the_board()
    {
        var board = new KanbanBoardViewModel([Card(_cardA, "A", "ready")]);

        Assert.Throws<ArgumentException>(() => board.Refuse(_cardB, "no"));
    }

    [Fact]
    public void Unknown_state_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() =>
            new KanbanBoardViewModel([Card(_cardA, "Odd", "blocked")]));
    }
}
