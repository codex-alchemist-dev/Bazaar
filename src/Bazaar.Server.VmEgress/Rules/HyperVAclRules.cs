using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.VmEgress.Rules;

/// <summary>One Hyper-V extended port ACL rule (<c>Add-VMNetworkAdapterExtendedAcl</c>).</summary>
/// <param name="Action">Allow or Deny.</param>
/// <param name="Direction">Inbound or Outbound, from the VM's point of view.</param>
/// <param name="RemoteAddress">Remote address or CIDR prefix.</param>
/// <param name="Protocol">TCP, or null for any protocol.</param>
/// <param name="RemotePort">Remote port, or null for any.</param>
/// <param name="Weight">Higher weights are evaluated first.</param>
/// <param name="Stateful">Whether return traffic of an allowed flow is permitted.</param>
public sealed record HyperVAclRule(string Action, string Direction, string RemoteAddress, string? Protocol, int? RemotePort, int Weight, bool Stateful);

/// <summary>
/// Pure generator for the Hyper-V side of default-deny (doc 16 WP9, layer 1): allow the guest to reach the proxy address and port and
/// nothing else, both directions, IPv4 and IPv6. Execution is not here; it is behind <see cref="Bazaar.Server.VmEgress.IEgressEnforcer"/>.
/// </summary>
public static partial class HyperVAclRules
{
    /// <summary>Weight of the allow rules. They sit above the deny rules.</summary>
    public const int AllowWeight = 100;

    /// <summary>Weight of the deny-all rules.</summary>
    public const int DenyWeight = 1;

    /// <summary>The rules for <paramref name="rules"/>: two allows for the proxy, then deny-all in both directions for IPv4 and IPv6.</summary>
    public static IReadOnlyList<HyperVAclRule> Generate(EgressRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.ProxyPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "The proxy port must be bound (1 to 65535) before rules are generated.");
        }

        var proxy = Canonical(rules.ProxyAddress);
        return
        [
            new("Allow", "Outbound", proxy, "TCP", rules.ProxyPort, AllowWeight, true),
            new("Allow", "Inbound", proxy, "TCP", rules.ProxyPort, AllowWeight, true),
            .. DenyAll(),
        ];
    }

    /// <summary>The kill-switch rule set: deny everything, the proxy included.</summary>
    public static IReadOnlyList<HyperVAclRule> GenerateBlockAll() => DenyAll();

    /// <summary>
    /// PowerShell for one adapter: removes the adapter's earlier extended ACLs, then adds <paramref name="acls"/>. The VM name is
    /// validated and single-quoted; addresses come from parsed values, never from caller text.
    /// </summary>
    public static string ToPowerShell(string vmName, IReadOnlyList<HyperVAclRule> acls)
    {
        ArgumentNullException.ThrowIfNull(acls);
        if (vmName is null || !VmNamePattern().IsMatch(vmName))
        {
            throw new ArgumentException("VM name has characters that are not allowed.", nameof(vmName));
        }

        var q = "'" + vmName + "'";
        var text = new StringBuilder();
        text.Append("Get-VMNetworkAdapterExtendedAcl -VMName ").Append(q).Append(" | Remove-VMNetworkAdapterExtendedAcl\n");
        foreach (var acl in acls)
        {
            text.Append("Add-VMNetworkAdapterExtendedAcl -VMName ").Append(q)
                .Append(" -Action ").Append(acl.Action)
                .Append(" -Direction ").Append(acl.Direction)
                .Append(" -RemoteIPAddress '").Append(acl.RemoteAddress).Append('\'');
            if (acl.Protocol is not null)
            {
                text.Append(" -Protocol ").Append(acl.Protocol);
            }

            if (acl.RemotePort is { } port)
            {
                text.Append(" -RemotePort ").Append(port.ToString(CultureInfo.InvariantCulture));
            }

            text.Append(" -Weight ").Append(acl.Weight.ToString(CultureInfo.InvariantCulture))
                .Append(" -Stateful ").Append(acl.Stateful ? "$true" : "$false").Append('\n');
        }

        return text.ToString();
    }

    private static HyperVAclRule[] DenyAll() =>
    [
        new("Deny", "Outbound", "0.0.0.0/0", null, null, DenyWeight, false),
        new("Deny", "Inbound", "0.0.0.0/0", null, null, DenyWeight, false),
        new("Deny", "Outbound", "::/0", null, null, DenyWeight, false),
        new("Deny", "Inbound", "::/0", null, null, DenyWeight, false),
    ];

    private static string Canonical(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? address.ToString()
            : throw new ArgumentException("The proxy address must be IPv4 or IPv6.", nameof(address));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._ -]{0,99}$")]
    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._ -]{0,99}\\z")]
    private static partial Regex VmNamePattern();
}
