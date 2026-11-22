namespace Bazaar.Server.VmEgress.Approvals;

/// <summary>
/// Operator settings for the egress approval gate. The tier itself is <c>vm.egress.allowlist_change_approval</c>, which the policy
/// service passes in. Nothing here can loosen the floor or the fail-closed rules.
/// </summary>
public sealed record EgressApprovalGateOptions
{
    /// <summary>Lowest tier the gate asks for. A lower caller tier is raised to this (doc 16 floor: Tier 2).</summary>
    public const int TierFloor = 2;

    /// <summary>
    /// Longest the gate waits for the taint reader or the approvals service, each call on its own. A slow service means denied.
    /// The owner's decision itself is not awaited: the gate answers Pending and is asked again.
    /// </summary>
    public TimeSpan PortTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Checks the ranges.</summary>
    public void Validate()
    {
        if (PortTimeout <= TimeSpan.Zero || PortTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(PortTimeout), PortTimeout, "The port timeout must be above zero and at most 5 minutes.");
        }
    }
}
