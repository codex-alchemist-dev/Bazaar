namespace Bazaar.Server.Agents.Scheduling;

/// <summary>
/// A check the wake loop makes before it asks the loop guard or starts a turn for an agent. The server host answers it from
/// the kill switch (doc 15): an engaged global stop or an agent stop denies the wake. A denied agent keeps its inbox items.
/// </summary>
public interface IAgentWakeGate
{
    /// <summary>Whether the agent may be woken now. Does no I/O.</summary>
    WakeGateDecision Check(Guid agentId);
}

/// <summary>The answer of an <see cref="IAgentWakeGate"/>.</summary>
/// <param name="Allowed">True when the agent may be woken.</param>
/// <param name="Reason">Why the wake was denied. Null when allowed.</param>
public readonly record struct WakeGateDecision(bool Allowed, string? Reason)
{
    /// <summary>The wake may go ahead.</summary>
    public static WakeGateDecision Allow { get; } = new(true, null);

    /// <summary>The wake is denied for <paramref name="reason"/>.</summary>
    public static WakeGateDecision Deny(string reason) => new(false, reason);
}
