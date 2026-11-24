using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.VmEgress.Approvals.Security;

/// <summary>
/// <see cref="IEgressTaintReader"/> over Security's <see cref="IAgentSessionTaintReader"/> (worst taint across the agent's active
/// sessions, doc 15 "Reads for modules"). Hard taint blocks egress growth (a Tier 2 action); soft taint does not. An agent with no
/// active session is clean. Without a registered reader every call throws, so the gate denies.
/// </summary>
public sealed class AgentTaintReader(IAgentSessionTaintReader? sessions) : IEgressTaintReader
{
    /// <inheritdoc />
    public async ValueTask<bool> IsHardTaintedAsync(string agentId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (sessions is null)
        {
            throw new InvalidOperationException("No agent session taint reader is registered for egress growth.");
        }

        var state = await sessions.GetActiveTaintAsync(agentId, ct).ConfigureAwait(false);
        return state == TaintState.Hard;
    }
}
