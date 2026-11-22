using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio;

/// <summary>
/// <see cref="IRelayResultSender"/> over the client's wire connection: sends the <see cref="EngineRelayWire.RequestType"/> request that
/// answers one relay event and waits for the server's <c>ok</c>. The wire client resends an unanswered request with the same
/// envelope id after a reconnect, which the server answers once, so a retry cannot deliver the answer twice. A server <c>err</c>
/// (unknown, expired, foreign or repeated relay id) throws <see cref="WireRequestException"/>; the loop reports it and moves on.
/// </summary>
public sealed class WireRelayResultSender(WireClient client) : IRelayResultSender
{
    /// <inheritdoc />
    public ValueTask SendAsync(EngineRelayResultRequest result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ValueTask(client.SendRequestAsync(EngineRelayWire.RequestType, null, EngineRelayFrameMapping.ToFrame(result), cancellationToken));
    }
}
