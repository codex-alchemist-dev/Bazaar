using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.RyuseiFuda.Kanban.Wire;

/// <summary>
/// <c>board.assign</c> (doc 12, Assign authority and command [PROPOSED]): the owner sets or clears one card's assignee from a client.
/// It calls <see cref="TaskService.AssignAsync"/> as the owner principal, so the version check, the authority rule and the
/// assignment inbox item are the same as for every other caller. Only the owner principal may assign; any other principal gets
/// <c>not_found</c>, as <c>board.move</c> does. Every outcome is a <see cref="KanbanAssignReply"/>; only a malformed payload is a wire error.
/// </summary>
public sealed class BoardAssignHandler(TaskService tasks) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.BoardAssign;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        KanbanAssignRequest request;
        try
        {
            request = context.Payload<KanbanAssignRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The assign request does not fit its schema.");
        }

        var principal = context.Connection.Principal;
        if (!string.Equals(principal.Kind, "owner", StringComparison.Ordinal))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var codec = context.Connection.Codec;
        if (request.ProjectId == Guid.Empty || request.CardId == Guid.Empty || request.AssigneeAgentId == Guid.Empty)
        {
            return Reply(codec, KanbanAssignStatuses.Invalid, null, "An assignment needs a project id and a card id.");
        }

        if (request.ExpectedRevision < 1)
        {
            return Reply(codec, KanbanAssignStatuses.Invalid, null, "The expected revision must be 1 or more.");
        }

        var card = await tasks.GetTaskAsync(request.CardId, cancellationToken).ConfigureAwait(false);
        if (card is null || card.ProjectId != request.ProjectId)
        {
            return Reply(codec, KanbanAssignStatuses.Invalid, null, "No such card in this project.");
        }

        // The actor comes from the connection, never from the payload.
        var actor = new TaskActor(TaskActorKind.Owner, principal.Id);
        var result = await tasks.AssignAsync(request.CardId, request.AssigneeAgentId, actor, request.ExpectedRevision, cancellationToken)
            .ConfigureAwait(false);

        return result.Status switch
        {
            TransitionStatus.Applied => Reply(codec, KanbanAssignStatuses.Applied, result.Version, null),
            TransitionStatus.Conflict => Reply(codec, KanbanAssignStatuses.Conflict, result.Version, null),
            TransitionStatus.Refused => Reply(codec, KanbanAssignStatuses.Refused, null, result.Outcome == TransitionOutcome.NotAuthorised
                ? "You may not assign that card."
                : "The card already has that assignee."),
            _ => Reply(codec, KanbanAssignStatuses.Invalid, null, "No such card."),
        };
    }

    private static WireReply Reply(IWireCodec codec, string status, long? revision, string? reason) =>
        WireReply.Ok(codec, new KanbanAssignReply(status, revision, reason));
}
