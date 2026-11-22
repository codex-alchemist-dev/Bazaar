namespace Bazaar.Server.VmEgress;

/// <summary>
/// Puts default-deny in place outside the guest: Hyper-V extended port ACLs on Windows, nftables on a Linux bridge (doc 16 WP9).
/// The rules come from the pure generators in <c>Bazaar.Server.VmEgress</c>; executing them needs the host (a Hyper-V runspace or a
/// root helper), so it lives behind this interface. Implementations must be idempotent and must replace, not add to, earlier rules.
/// </summary>
public interface IEgressEnforcer
{
    /// <summary>
    /// Allows the guest to reach only <c>rules.ProxyAddress:rules.ProxyPort</c> (both directions, IPv4 and IPv6) and denies the rest.
    /// <see cref="EgressRules.ProxyPort"/> is the bound port, never 0. Throws when the rules could not be put in force.
    /// </summary>
    Task ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default);

    /// <summary>Replaces the allow rule with deny-all in both directions, proxy included.</summary>
    Task BlockAllAsync(string agentId, CancellationToken ct = default);
}
