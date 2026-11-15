using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Kanban.Wire;

/// <summary>
/// <c>board.snapshot</c>: answers with the project's whole board (doc 12). A denied read and a missing project get the same
/// <c>not_found</c>, so membership does not leak. The client re-requests the snapshot on each board event; there is no
/// live stream yet. Access is decided by <see cref="IBoardAccessPolicy"/>, never by the request payload.
/// </summary>
public sealed class BoardSnapshotHandler(TaskService tasks, IBoardAccessPolicy access) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.BoardSnapshot;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        KanbanBoardRequest request;
        try
        {
            request = context.Payload<KanbanBoardRequest>();
        }
        catch (Bazaar.Contracts.Wire.Codec.WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The board request does not fit its schema.");
        }

        if (request.ProjectId == Guid.Empty)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A board request needs a project id.");
        }

        if (!await access.CanReadAsync(context.Connection.Principal, request.ProjectId, cancellationToken).ConfigureAwait(false))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var snapshot = await tasks.GetBoardSnapshotAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        return WireReply.Ok(context.Connection.Codec, snapshot);
    }
}
