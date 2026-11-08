using Bazaar.Contracts.Kanban;

namespace Bazaar.Client.Core.Kanban;

/// <summary>
/// Maps the server's board snapshot (doc 12, board view) into the client board. The ids arrive as Guids, so the
/// mapping only copies fields; it does not parse anything. Avalonia-free.
/// </summary>
public static class KanbanBoardMapping
{
    /// <summary>The cards of a snapshot, in the snapshot's order.</summary>
    public static IReadOnlyList<KanbanCard> ToCards(KanbanBoardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Cards
            .Select(card => new KanbanCard(
                card.Id,
                card.Title,
                card.State,
                card.OwnerAgentId,
                card.UnfinishedBlockerIds,
                card.BlockerTitles))
            .ToList();
    }

    /// <summary>The board for a snapshot: five columns, with Blocked derived from unfinished blockers.</summary>
    /// <exception cref="ArgumentException">A card has a state that is not one of the five.</exception>
    public static KanbanBoardViewModel ToBoard(KanbanBoardSnapshot snapshot) =>
        new(ToCards(snapshot));
}
