using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.VmEgress.Rules;

/// <summary>
/// Pure generator for the Linux side of default-deny (doc 16 WP9, layer 1): an nftables <c>bridge</c> table that lets the guest's
/// interface reach only the proxy address and port, in both directions, IPv4 and IPv6, and drops everything else for that
/// interface. Rules are scoped to the one interface, so other bridge ports are untouched.
/// </summary>
public static partial class NftablesRuleset
{
    /// <summary>The ruleset text for <paramref name="rules"/>, to be loaded with <c>nft -f</c>. Deleting the table first makes it replace any earlier one.</summary>
    public static string Generate(string agentTag, string guestInterface, EgressRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.ProxyPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "The proxy port must be bound (1 to 65535) before rules are generated.");
        }

        var family = rules.ProxyAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => "ip",
            AddressFamily.InterNetworkV6 => "ip6",
            _ => throw new ArgumentException("The proxy address must be IPv4 or IPv6.", nameof(rules)),
        };
        var address = rules.ProxyAddress.ToString();
        var port = rules.ProxyPort.ToString(CultureInfo.InvariantCulture);
        var iface = Quote(guestInterface);

        var text = Header(agentTag);
        text.Append("  chain input {\n")
            .Append("    type filter hook input priority -200; policy accept;\n")
            .Append("    iifname ").Append(iface).Append(" ct state established,related accept\n")
            .Append("    iifname ").Append(iface).Append(" ether type arp accept\n")
            .Append("    iifname ").Append(iface).Append(' ').Append(family).Append(" daddr ").Append(address).Append(" tcp dport ").Append(port).Append(" accept\n")
            .Append("    iifname ").Append(iface).Append(" drop\n")
            .Append("  }\n")
            .Append("  chain output {\n")
            .Append("    type filter hook output priority -200; policy accept;\n")
            .Append("    oifname ").Append(iface).Append(" ct state established,related accept\n")
            .Append("    oifname ").Append(iface).Append(" ether type arp accept\n")
            .Append("    oifname ").Append(iface).Append(' ').Append(family).Append(" saddr ").Append(address).Append(" tcp sport ").Append(port).Append(" accept\n")
            .Append("    oifname ").Append(iface).Append(" drop\n")
            .Append("  }\n")
            .Append("  chain forward {\n")
            .Append("    type filter hook forward priority -200; policy accept;\n")
            .Append("    iifname ").Append(iface).Append(" drop\n")
            .Append("    oifname ").Append(iface).Append(" drop\n")
            .Append("  }\n")
            .Append("}\n");
        return text.ToString();
    }

    /// <summary>The kill-switch ruleset: every packet to or from the guest interface is dropped, ARP and the proxy included.</summary>
    public static string GenerateBlockAll(string agentTag, string guestInterface)
    {
        var iface = Quote(guestInterface);
        var text = Header(agentTag);
        foreach (var (chain, hook, dir) in new[] { ("input", "input", "iifname"), ("output", "output", "oifname") })
        {
            text.Append("  chain ").Append(chain).Append(" {\n")
                .Append("    type filter hook ").Append(hook).Append(" priority -200; policy accept;\n")
                .Append("    ").Append(dir).Append(' ').Append(iface).Append(" drop\n")
                .Append("  }\n");
        }

        text.Append("  chain forward {\n")
            .Append("    type filter hook forward priority -200; policy accept;\n")
            .Append("    iifname ").Append(iface).Append(" drop\n")
            .Append("    oifname ").Append(iface).Append(" drop\n")
            .Append("  }\n")
            .Append("}\n");
        return text.ToString();
    }

    private static StringBuilder Header(string agentTag)
    {
        if (agentTag is null || !TagPattern().IsMatch(agentTag))
        {
            throw new ArgumentException("Agent tag must be 1 to 32 letters, digits, underscores.", nameof(agentTag));
        }

        var table = "bazaar_egress_" + agentTag;
        return new StringBuilder()
            .Append("add table bridge ").Append(table).Append('\n')
            .Append("delete table bridge ").Append(table).Append('\n')
            .Append("table bridge ").Append(table).Append(" {\n");
    }

    private static string Quote(string guestInterface)
    {
        if (guestInterface is null || !InterfacePattern().IsMatch(guestInterface))
        {
            throw new ArgumentException("Interface name must be 1 to 15 letters, digits, dot, dash or underscore.", nameof(guestInterface));
        }

        return "\"" + guestInterface + "\"";
    }

    [GeneratedRegex("\\A[A-Za-z0-9_]{1,32}\\z")]
    private static partial Regex TagPattern();

    [GeneratedRegex("\\A[A-Za-z0-9_.-]{1,15}\\z")]
    private static partial Regex InterfacePattern();
}
