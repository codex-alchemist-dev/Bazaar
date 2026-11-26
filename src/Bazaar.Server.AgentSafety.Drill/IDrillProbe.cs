namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// Reads the containment effects of one agent: revoked tokens, paused VMs and blocked egress. The host implements it over the
/// containment and VM state. Tests use fakes.
/// </summary>
public interface IDrillProbe
{
    /// <summary>Observes the containment effects for the agent. Must not change any state.</summary>
    Task<DrillObservation> ObserveAsync(string agentId, CancellationToken ct = default);
}

/// <summary>The containment effects observed for one agent.</summary>
/// <param name="TokensRevoked">True when the agent's leases and tokens are revoked.</param>
/// <param name="VmsPaused">True when the agent's VMs are paused.</param>
/// <param name="EgressBlocked">True when the agent's egress is blocked.</param>
public sealed record DrillObservation(bool TokensRevoked, bool VmsPaused, bool EgressBlocked)
{
    /// <summary>VMs run and egress is open. Tokens are not part of this: revoked leases are not restored on release, so they stay revoked.</summary>
    public bool IsOpen => !VmsPaused && !EgressBlocked;

    /// <summary>Every containment effect is in place.</summary>
    public bool IsContained => TokensRevoked && VmsPaused && EgressBlocked;
}
