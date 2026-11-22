namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// The phone's view of the Kanban board service (doc 12, doc 29 work package 9). The head supplies the implementation;
/// the pane never builds a request itself. Tests use a fake.
/// </summary>
public interface IKanbanBoardClient
{
    /// <summary>Reads every card of a project's board, with each card's current revision.</summary>
    Task<KanbanPhoneBoard> LoadAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Moves one card to <paramref name="toState"/>, applied only when the card is still at <paramref name="expectedRevision"/>.
    /// Refusals and conflicts are results, not exceptions.
    /// </summary>
    Task<KanbanMoveResult> MoveAsync(Guid projectId, Guid cardId, string toState, long expectedRevision, CancellationToken ct);
}
