using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Connection;

/// <summary>Answers a client's application-level <c>ping</c> with <c>pong</c>.</summary>
public sealed class PingHandler : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.Ping;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var nonce = context.Request.P.IsEmpty ? null : context.Payload<Ping>().Nonce;
        return ValueTask.FromResult(WireReply.Ok(context.Connection.Codec, new Pong(nonce)));
    }
}
