using Bazaar.Client.Core.Kanban;
using Bazaar.Contracts.Kanban;

namespace Bazaar.Client.Core.Tests.Kanban;

public class KanbanBoardMappingTests
{
    private static readonly Guid _blocked = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");
    private static readonly Guid _blocker = Guid.Parse("0196a6f0-0000-7000-8000-000000000302");
    private static readonly Guid _owner = Guid.Parse("0196a6f0-0000-7000-8000-000000000303");

    private static KanbanBoardSnapshot Snapshot() => new(
        Guid.Parse("0196a6f0-0000-7000-8000-000000000300"),
        [
            new KanbanBoardCard(_blocked, "Wire the API", "backlog", _owner, [_blocker], ["Write the schema"]),
            new KanbanBoardCard(_blocker, "Write the schema", "ready", null, [], []),
        ]);

    [Fact]
    public void ToCards_copies_ids_owner_and_blockers_without_changing_them()
    {
        var cards = KanbanBoardMapping.ToCards(Snapshot());

        var card = Assert.Single(cards, c => c.Id == _blocked);
        Assert.Equal("Wire the API", card.Title);
        Assert.Equal("backlog", card.State);
        Assert.Equal(_owner, card.OwnerAgentId);
        Assert.Equal([_blocker], card.UnfinishedBlockerIds);
        Assert.Equal(["Write the schema"], card.BlockerTitles);
    }

    [Fact]
    public void ToBoard_puts_each_card_in_its_column_and_marks_the_blocked_one()
    {
        var board = KanbanBoardMapping.ToBoard(Snapshot());

        var backlog = Assert.Single(board.Columns, c => c.State == "backlog");
        var blocked = Assert.Single(backlog.Cards);
        Assert.Equal(_blocked, blocked.Id);
        Assert.True(blocked.Blocked);
        Assert.Equal(["Write the schema"], blocked.BlockerChips);

        var ready = Assert.Single(board.Columns, c => c.State == "ready");
        Assert.False(Assert.Single(ready.Cards).Blocked);
    }

    [Fact]
    public void ToCards_rejects_a_null_snapshot()
    {
        Assert.Throws<ArgumentNullException>(() => KanbanBoardMapping.ToCards(null!));
    }
}
