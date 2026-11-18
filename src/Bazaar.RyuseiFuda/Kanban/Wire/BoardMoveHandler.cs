using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Kanban.Wire;

/// <summary>
/// <c>board.move</c> (doc 12, Card revision and client move command): the owner moves one card from a client. It is the
/// "authorised UI command" of Interception at tool dispatch, so it calls <see cref="TaskService.TransitionAsync"/> as the owner
/// principal and the same table, blocker guard, version check and PR-merge rule apply as for every other caller. Only the
/// owner principal may move; any other principal gets <c>not_found</c>, as <c>board.snapshot</c> does, so a card id does not
/// leak. Every outcome of a move is a <see cref="KanbanMoveReply"/>; only a malformed payload is a wire error.
/// </summary>
public sealed class BoardMoveHandler(TaskService tasks) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.BoardMove;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        KanbanMoveRequest request;
        try
        {
            request = context.Payload<KanbanMoveRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The move request does not fit its schema.");
        }

        var principal = context.Connection.Principal;
        if (!string.Equals(principal.Kind, "owner", StringComparison.Ordinal))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var codec = context.Connection.Codec;
        if (request.ProjectId == Guid.Empty || request.CardId == Guid.Empty)
        {
            return Invalid(codec, "A move needs a project id and a card id.");
        }

        if (request.ExpectedRevision < 1)
        {
            return Invalid(codec, "The expected revision must be 1 or more.");
        }

        if (!TryParseState(request.ToState, out var to))
        {
            return Invalid(codec, "The target state must be one of backlog, ready, in_progress, review, done.");
        }

        var card = await tasks.GetTaskAsync(request.CardId, cancellationToken).ConfigureAwait(false);
        if (card is null || card.ProjectId != request.ProjectId)
        {
            return Invalid(codec, "No such card in this project.");
        }

        // The actor comes from the connection, never from the payload.
        var actor = new TaskActor(TaskActorKind.Owner, principal.Id);
        var result = await tasks.TransitionAsync(request.CardId, to, actor, request.Reason, request.ExpectedRevision, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case TransitionStatus.Applied:
                return WireReply.Ok(codec, new KanbanMoveReply(KanbanMoveStatuses.Applied, result.Version, []));
            case TransitionStatus.Conflict:
                return WireReply.Ok(codec, new KanbanMoveReply(KanbanMoveStatuses.Conflict, result.Version, []));
            case TransitionStatus.Refused:
                var titles = result.UnfinishedBlockers.Count == 0
                    ? []
                    : await BlockerTitlesAsync(result.UnfinishedBlockers, cancellationToken).ConfigureAwait(false);
                return WireReply.Ok(codec, new KanbanMoveReply(KanbanMoveStatuses.Refused, null, titles, ReasonText(result.Outcome)));
            default:
                return Invalid(codec, ReasonText(result.Outcome));
        }
    }

    private async Task<IReadOnlyList<string>> BlockerTitlesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var known = await tasks.GetTitlesAsync(ids, ct).ConfigureAwait(false);
        return ids.Select(id => known.TryGetValue(id, out var title) ? title : "(unknown task)").ToList();
    }

    private static WireReply Invalid(IWireCodec codec, string reason) =>
        WireReply.Ok(codec, new KanbanMoveReply(KanbanMoveStatuses.Invalid, null, [], reason));

    private static bool TryParseState(string? text, out TaskState state)
    {
        switch (text)
        {
            case "backlog": state = TaskState.Backlog; return true;
            case "ready": state = TaskState.Ready; return true;
            case "in_progress": state = TaskState.InProgress; return true;
            case "review": state = TaskState.Review; return true;
            case "done": state = TaskState.Done; return true;
            default: state = default; return false;
        }
    }

    /// <summary>Words the owner can read for a refused or invalid outcome. Never names an internal field.</summary>
    internal static string ReasonText(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.NotATransition => "That move is not allowed on the board.",
        TransitionOutcome.UnknownTask => "No such card.",
        TransitionOutcome.NotAuthorised => "You may not make that move.",
        TransitionOutcome.MissingReason => "That move needs a reason.",
        TransitionOutcome.NeedsLinks => "The card needs a linked PR or commit before it goes to Review.",
        TransitionOutcome.PrNotMerged => "A card with a PR is Done only when its PR is merged.",
        TransitionOutcome.GateNotSatisfied => "The card's approval gate has not been satisfied.",
        TransitionOutcome.BlockersUnfinished => "The card is blocked by unfinished prerequisites.",
        _ => "The move was refused.",
    };
}
