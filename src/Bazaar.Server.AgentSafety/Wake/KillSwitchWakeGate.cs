using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Wake;

/// <summary>
/// Answers the wake loop's <see cref="IAgentWakeGate"/> from the kill switch (doc 15). A wake changes state, so it is asked as
/// <see cref="ToolEffect.Write"/>: a global stop and an agent stop both deny it. The engine itself fails closed until its state has
/// loaded. With no gate registered at all, the wake loop denies every wake.
/// </summary>
public sealed class KillSwitchWakeGate(KillSwitchEngine engine) : IAgentWakeGate
{
    /// <inheritdoc />
    public WakeGateDecision Check(Guid agentId)
    {
        var decision = engine.Evaluate(agentId.ToString("D"), ToolEffect.Write);
        return decision.Allowed ? WakeGateDecision.Allow : WakeGateDecision.Deny(decision.Reason ?? "The kill switch is engaged.");
    }
}
