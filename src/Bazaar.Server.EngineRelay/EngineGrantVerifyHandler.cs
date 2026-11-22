using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.EngineRelay;

/// <summary>
/// Handles the client's <c>engine.grant.verify</c> request [PROPOSED, 2026-11-22] (doc 02, doc 20). The Studio bridge asks before it runs
/// a relayed call; the answer is yes only when the tool is known at the stated risk class and the broker still has a live relay on
/// this device for exactly that grant and tool (the dispatcher authorised it before it was published). Only the owner's Windows
/// desktop may ask. Any bad request is a plain <c>false</c> or an <c>err</c> with a content-free message; the bridge fails closed on both.
/// </summary>
public sealed class EngineGrantVerifyHandler(EngineRelayBroker broker) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => EngineRelayWire.GrantVerifyType;

    /// <inheritdoc />
    public ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RelayDevicePolicy.IsTarget(context.Connection) || context.Request.P.IsEmpty)
        {
            return ValueTask.FromResult(WireReply.Fail(WireErrorCodes.Unauthorized, "not accepted"));
        }

        EngineGrantVerifyRequest request = context.Payload<EngineGrantVerifyRequest>();
        bool covers = StudioToolCatalog.TryGetRiskClass(request.Tool, out RiskClass actual)
            && actual == request.RiskClass
            && broker.IsLiveCall(context.Connection.DeviceId, request.GrantId, request.Tool);
        return ValueTask.FromResult(WireReply.Ok(context.Connection.Codec, new EngineGrantVerifyReply(covers)));
    }
}
