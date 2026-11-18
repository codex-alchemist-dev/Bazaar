using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// <see cref="IKanbanBoardClient"/> over the wire (doc 12, Card revision and client move command): <c>board.snapshot</c> to
/// load and <c>board.move</c> to move. Each move outcome is a <see cref="KanbanMoveResult"/>. A schema rejection
/// (<c>bad_request</c>) is <see cref="KanbanMoveResult.Invalid"/>; a lost connection or any other wire error is thrown as the
/// <see cref="WireRequestException"/> or transport exception, so the pane keeps the board as it was.
/// </summary>
public sealed class WireKanbanBoardClient(IWireRequester requests) : IKanbanBoardClient
{
    /// <inheritdoc />
    public async Task<KanbanPhoneBoard> LoadAsync(Guid projectId, CancellationToken ct)
    {
        var snapshot = await requests.RequestAsync<KanbanBoardRequest, KanbanBoardSnapshot>(
            WireTypes.BoardSnapshot, new KanbanBoardRequest(projectId), ct).ConfigureAwait(false);

        var cards = snapshot.Cards
            .Select(card => new KanbanPhoneCard(card.Id, card.Title, card.State, card.Revision, card.UnfinishedBlockerIds, card.BlockerTitles))
            .ToList();
        return new KanbanPhoneBoard(snapshot.ProjectId, cards);
    }

    /// <inheritdoc />
    public async Task<KanbanMoveResult> MoveAsync(Guid projectId, Guid cardId, string toState, long expectedRevision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(toState);

        KanbanMoveReply reply;
        try
        {
            reply = await requests.RequestAsync<KanbanMoveRequest, KanbanMoveReply>(
                WireTypes.BoardMove, new KanbanMoveRequest(projectId, cardId, toState, expectedRevision), ct).ConfigureAwait(false);
        }
        catch (WireRequestException ex) when (ex.Error.Code == WireErrorCodes.BadRequest)
        {
            return new KanbanMoveResult.Invalid(ex.Error.Message ?? "The server did not accept the move.");
        }

        return Map(reply);
    }

    /// <summary>Maps a wire reply onto the pane's result. An applied or conflicting reply without a revision is a broken server, so it is invalid.</summary>
    internal static KanbanMoveResult Map(KanbanMoveReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return reply.Status switch
        {
            KanbanMoveStatuses.Applied when reply.Revision is { } revision => new KanbanMoveResult.Applied(revision),
            KanbanMoveStatuses.Conflict when reply.Revision is { } revision => new KanbanMoveResult.Conflict(revision),
            KanbanMoveStatuses.Refused => new KanbanMoveResult.Refused(reply.BlockerTitles ?? [], reply.Reason ?? "The server refused the move."),
            KanbanMoveStatuses.Invalid => new KanbanMoveResult.Invalid(reply.Reason ?? "The server did not accept the move."),
            _ => new KanbanMoveResult.Invalid("The server's answer to the move was not understood."),
        };
    }
}
