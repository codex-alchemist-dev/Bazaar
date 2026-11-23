using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// Handles the client's <c>engine.relay.result</c> request (doc 02). Only the owner's Windows desktop may answer. The broker
/// accepts an answer once and only from the device the event went to. Everything else is answered <c>err</c> with a content-free
/// message, so a probe learns nothing about which relay ids exist.
/// </summary>
public sealed class EngineRelayResultHandler(EngineRelayBroker broker) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => EngineRelayWire.RequestType;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RelayDevicePolicy.IsTarget(context.Connection) || context.Request.P.IsEmpty)
        {
            return ValueTask.FromResult(WireReply.Fail(WireErrorCodes.Unauthorized, "not accepted"));
        }

        EngineRelayResultRequest answer;
        try
        {
            answer = EngineRelayFrameMapping.ToRequest(context.Payload<EngineRelayResultFrame>());
        }
        catch (FormatException)
        {
            return ValueTask.FromResult(WireReply.Fail(WireErrorCodes.BadRequest, "not accepted"));
        }

        EngineRelaySubmitStatus status = broker.SubmitResult(context.Connection.DeviceId, answer.RelayId, answer.Result);
        return ValueTask.FromResult(status == EngineRelaySubmitStatus.Accepted
            ? WireReply.Ok()
            : WireReply.Fail(WireErrorCodes.NotFound, "not accepted"));
    }
}
