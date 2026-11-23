using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.RyuseiFuda.Kanban.Health;
using Xunit;

namespace Bazaar.Server.Tests.Kanban.Health;

public sealed class KanbanHealthFactsTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static TaskRecord Task(TaskState state) =>
        new(Guid.NewGuid(), Project, "t", state, null, TaskGate.None, Guid.NewGuid(), null, null, null, null, 1, Now, Now);

    [Fact]
    public void Reopens_are_moves_out_of_done_only()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var facts = KanbanHealthFacts.Build([], [], [
            new TaskTransitionFact(a, "done", "in_progress", Now),
            new TaskTransitionFact(b, "review", "done", Now),
            new TaskTransitionFact(b, "ready", "in_progress", Now),
        ], includeBoard: false);

        var reopen = Assert.Single(facts.Reopens);
        Assert.Equal(a.ToString("D"), reopen.CardId);
        Assert.Null(facts.Board);
    }

    [Fact]
    public void Board_counts_open_cards_and_blocked_ones()
    {
        var blocker = Task(TaskState.InProgress);
        var blocked = Task(TaskState.Ready);
        var free = Task(TaskState.Backlog);
        var done = Task(TaskState.Done);
        var facts = KanbanHealthFacts.Build(
            [blocker, blocked, free, done],
            [new TaskEdge(blocker.Id, blocked.Id)],
            [],
            includeBoard: true);

        Assert.NotNull(facts.Board);
        Assert.Equal(3, facts.Board!.TotalCards);
        Assert.Equal(1, facts.Board.BlockedCards);
    }

    [Fact]
    public void A_finished_blocker_does_not_block()
    {
        var blocker = Task(TaskState.Done);
        var blocked = Task(TaskState.Ready);
        var facts = KanbanHealthFacts.Build([blocker, blocked], [new TaskEdge(blocker.Id, blocked.Id)], [], includeBoard: true);

        Assert.Equal(0, facts.Board!.BlockedCards);
        Assert.Equal(1, facts.Board.TotalCards);
    }

    [Fact]
    public void A_board_with_no_open_card_is_a_gap()
    {
        var facts = KanbanHealthFacts.Build([Task(TaskState.Done)], [], [], includeBoard: true);
        Assert.Null(facts.Board);
    }
}
