namespace Bazaar.Secrets;

/// <summary>
/// The owner's release of an agent from quarantine. A quarantined agent may return to Active only with one of these, so the
/// release always names who gave it and why.
/// </summary>
public sealed record OwnerRelease
{
    public OwnerRelease(string owner, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Owner = owner;
        Reason = reason;
    }

    public string Owner { get; }

    public string Reason { get; }
}

/// <summary>Raised when a state change breaks the transition rules. Nothing is changed and nothing is audited as a transition.</summary>
public sealed class AgentTransitionException(string message) : InvalidOperationException(message);

/// <summary>
/// Which agent state changes are allowed (doc 14 kill switch and quarantine). Pure rules, shared by every registry.
/// Active to Quarantined, and any state to Revoked, are allowed. Quarantined to Active needs an owner release.
/// Revoked is terminal: a revoked agent gets a new identity instead of being revived.
/// </summary>
public static class AgentTransitionRules
{
    /// <summary>Returns null when the change is allowed, otherwise the reason it is refused. Same-state changes are not checked here.</summary>
    public static string? Denial(AgentState from, AgentState to, OwnerRelease? release)
    {
        if (from == AgentState.Revoked)
        {
            return "a revoked agent is terminal; create a new identity instead";
        }

        if (to == AgentState.Revoked)
        {
            return null;
        }

        if (from == AgentState.Active && to == AgentState.Quarantined)
        {
            return null;
        }

        if (from == AgentState.Quarantined && to == AgentState.Active)
        {
            return release is null ? "leaving quarantine needs an owner release" : null;
        }

        return $"a change from {from} to {to} is not allowed";
    }
}
