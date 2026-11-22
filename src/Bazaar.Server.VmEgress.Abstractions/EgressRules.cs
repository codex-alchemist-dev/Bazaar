using System.Collections.Immutable;
using System.Net;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// One agent's egress rules (doc 15 "Egress and ingress", doc 16 WP9): the domain allowlist the host proxy enforces, and the one
/// address and port the guest may reach at all.
/// </summary>
/// <param name="AllowedDestinations">
/// Allowlist entries. <c>example.com</c> matches that name only; <c>*.example.com</c> matches any name below it, not the apex.
/// An entry may carry <c>:port</c>; without one it covers ports 80 and 443. An IP literal matches only when listed here.
/// </param>
/// <param name="ProxyAddress">Host address of the agent's proxy, as the guest sees it. The only destination the switch allows.</param>
/// <param name="ProxyPort">TCP port of the proxy. 0 asks the proxy host for an OS-assigned port; the enforcer always receives the real one.</param>
public sealed record EgressRules(ImmutableArray<string> AllowedDestinations, IPAddress ProxyAddress, int ProxyPort)
{
    /// <summary>Rules that allow no destination at all. The proxy refuses everything.</summary>
    public static EgressRules DenyAll(IPAddress proxyAddress, int proxyPort) => new([], proxyAddress, proxyPort);

    /// <summary>Same allowlist and address with another port. Used once the proxy has bound.</summary>
    public EgressRules WithProxyPort(int port) => this with { ProxyPort = port };

    /// <summary>Two rule sets are equal when address, port and the entries, in order, are equal.</summary>
    public bool Equals(EgressRules? other) =>
        other is not null
        && ProxyPort == other.ProxyPort
        && ProxyAddress.Equals(other.ProxyAddress)
        && AllowedDestinations.AsSpan().SequenceEqual(other.AllowedDestinations.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ProxyAddress, ProxyPort, AllowedDestinations.Length);
}
