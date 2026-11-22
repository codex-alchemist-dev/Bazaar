using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio;

/// <summary>
/// <see cref="IStudioGrantVerifier"/> over the wire [PROPOSED, 2026-11-22]: sends <c>engine.grant.verify</c> and returns the server's
/// answer. A wire error, a lost connection or a cancelled call throws, and the bridge then refuses the call (it fails closed).
/// </summary>
public sealed class WireStudioGrantVerifier(IWireRequester requests) : IStudioGrantVerifier
{
    /// <inheritdoc />
    public async Task<bool> CoversAsync(string grantId, string tool, RiskClass riskClass, CancellationToken cancellationToken)
    {
        var reply = await requests.RequestAsync<EngineGrantVerifyRequest, EngineGrantVerifyReply>(
            EngineRelayWire.GrantVerifyType, new EngineGrantVerifyRequest(grantId, tool, riskClass), cancellationToken).ConfigureAwait(false);
        return reply.Covers;
    }
}
