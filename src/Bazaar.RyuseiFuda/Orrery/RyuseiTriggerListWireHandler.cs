using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;
using Npgsql;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// <c>ryusei.trigger.list</c> (doc 38, Layer 2): the owner reads a project's triggers as board chips. Only the owner principal
/// may read them. Any other principal gets <c>not_found</c>, as the board does, so the project's triggers do not leak. The
/// client joins each chip to its card by task id, so the Kanban board snapshot stays unchanged.
/// </summary>
public sealed class RyuseiTriggerListWireHandler : IWireRequestHandler
{
    private readonly RyuseiTriggerRepository _triggers;

    /// <param name="dataSource">The database the trigger rows live in.</param>
    public RyuseiTriggerListWireHandler(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _triggers = new RyuseiTriggerRepository(dataSource);
    }

    /// <inheritdoc />
    public string Type => WireTypes.RyuseiList;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RyuseiTriggerListRequest request;
        try
        {
            request = context.Payload<RyuseiTriggerListRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "The trigger list request does not fit its schema.");
        }

        if (request.ProjectId == Guid.Empty)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A trigger list request needs a project id.");
        }

        if (context.Connection.Principal.Kind != "owner")
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        var rows = await _triggers.ListByProjectAsync(request.ProjectId, ct: cancellationToken).ConfigureAwait(false);
        var chips = rows
            .Select(row => new RyuseiTriggerChip(
                row.TriggerId,
                row.TaskId,
                RyuseiText.ToSql(row.Kind),
                RyuseiText.ToSql(row.State),
                row.NextFireAt,
                row.Purpose,
                row.Version))
            .ToArray();
        return WireReply.Ok(context.Connection.Codec, new RyuseiTriggerListReply(request.ProjectId, chips));
    }
}
