using Bazaar.Contracts.Memory;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Constellation.Memory.Context;

/// <summary>
/// <c>memory.context</c> (docs/design/constellation-memory-graph.md, "Retrieval operation"): the owner reads packed memory context of
/// one agent from a client. Wire principals are not agent identities, so only the owner principal may call it; agents call
/// <see cref="IMemoryContextService"/> in process from their own session. The owner's read of the agent's private area is audited and
/// logged by the service. Every refusal is a wire error with a <see cref="MemoryContextErrorCodes"/> code.
/// </summary>
public sealed class MemoryContextWireHandler(IMemoryContextService service) : IWireRequestHandler
{
    private readonly IMemoryContextService _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public string Type => WireTypes.MemoryContext;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.Connection.Principal;
        if (!string.Equals(principal.Kind, "owner", StringComparison.Ordinal))
        {
            return WireReply.Fail(MemoryContextErrorCodes.Unauthorized, "Only the owner may read memory context over the wire.");
        }

        MemoryContextRequest request;
        try
        {
            request = context.Payload<MemoryContextRequest>();
        }
        catch (WireFormatException)
        {
            return WireReply.Fail(MemoryContextErrorCodes.BadRequest, "The memory context request does not fit its schema.");
        }

        if (request is null)
        {
            return WireReply.Fail(MemoryContextErrorCodes.BadRequest, "The memory context request is empty.");
        }

        // The caller comes from the connection, never from the payload.
        var caller = MemoryContextCaller.Owner("owner:" + principal.Id.ToString("D"), context.Connection.ConnectionId.ToString("D"), WireTypes.MemoryContext);
        var outcome = await _service.RetrieveAsync(caller, request, cancellationToken).ConfigureAwait(false);
        return outcome.IsSuccess
            ? WireReply.Ok(context.Connection.Codec, outcome.Result!)
            : WireReply.Fail(outcome.ErrorCode!, outcome.Message);
    }
}
