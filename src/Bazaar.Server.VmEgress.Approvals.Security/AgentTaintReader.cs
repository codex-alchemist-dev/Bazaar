using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.VmEgress.Approvals.Security;

/// <summary>
/// <see cref="IEgressTaintReader"/> over <see cref="IAgentSessionTaintLookup"/>. Hard taint blocks egress growth (a Tier 2 action);
/// soft taint does not. Without a registered lookup every call throws, so the gate denies.
/// </summary>
public sealed class AgentTaintReader(IAgentSessionTaintLookup? lookup) : IEgressTaintReader
{
    /// <inheritdoc />
    public async ValueTask<bool> IsHardTaintedAsync(string agentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (lookup is null)
        {
            throw new InvalidOperationException("No agent session taint lookup is registered for egress growth.");
        }

        var taint = await lookup.GetAgentTaintAsync(agentId, ct).ConfigureAwait(false);
        return taint.State == TaintState.Hard;
    }
}
