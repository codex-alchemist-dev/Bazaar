using System.Text;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// Renders the pool VM's nftables ruleset as one document the host applies atomically (<c>nft -f</c>). It declares, deletes and
/// recreates only its own table, so it never touches another table.
/// <list type="bullet">
/// <item>Always (both isolation modes): each slot user may open TCP to its agent's proxy and nothing else, and every other packet
/// from any slot uid (loopback, UDP and DNS included) is dropped. This is the <c>output</c> chain.</item>
/// <item>Per-slot namespaces only: the <c>prerouting</c>, <c>postrouting</c>, <c>input</c> and <c>forward</c> chains. A packet from a
/// slot's veth to the VM-side veth address on the egress port is DNATed to the real endpoint; every other packet that arrives from
/// any <c>bzv*</c> veth is dropped, so a slot reaches nothing else in the VM or beyond it. The <c>output</c> chain sees only
/// traffic that starts in the VM's own namespace; it is a second layer for a slot-uid process there, not for the namespace's traffic.</item>
/// </list>
/// </summary>
public static class NftablesRules
{
    /// <summary>The table name, so the host can inspect or remove it.</summary>
    public const string TableName = "bazaar_pool";

    /// <summary>
    /// Renders the shared-namespace ruleset for the active rules over the uid range <paramref name="uidFirst"/> to
    /// <paramref name="uidLast"/>: the per-uid output chain only.
    /// </summary>
    /// <exception cref="ArgumentException">A proxy host is not an IPv4 literal, a port or uid is out of range, or a uid has two rules.</exception>
    public static string Render(IEnumerable<PoolFirewallRule> rules, int uidFirst, int uidLast)
    {
        var ordered = Validate(rules, uidFirst, uidLast);
        var sb = new StringBuilder();
        AppendHeader(sb);
        AppendOutputChain(sb, ordered, uidFirst, uidLast);
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the ruleset for the pool's configured isolation: the per-uid chain, and in per-slot mode the veth chains for every
    /// rule, with the slot taken from the rule's uid.
    /// </summary>
    /// <exception cref="ArgumentException">The options are invalid, or a rule fails the checks of the other overload.</exception>
    public static string Render(IEnumerable<PoolFirewallRule> rules, BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = BrowserPoolOptionsValidator.Check(options);
        if (errors.Count != 0)
        {
            throw new ArgumentException("Invalid browser pool options: " + string.Join(' ', errors), nameof(options));
        }

        var ordered = Validate(rules, options.SlotUidBase, options.MaxUid);
        var sb = new StringBuilder();
        AppendHeader(sb);
        AppendOutputChain(sb, ordered, options.SlotUidBase, options.MaxUid);
        if (options.NetworkIsolation == NetworkIsolation.PerSlotNamespace)
        {
            AppendVethChains(sb, ordered, options);
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static List<PoolFirewallRule> Validate(IEnumerable<PoolFirewallRule> rules, int uidFirst, int uidLast)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (uidFirst < 10000 || uidLast < uidFirst || uidLast > 59999)
        {
            throw new ArgumentException("The uid range is not acceptable.");
        }

        var seen = new HashSet<int>();
        var ordered = new List<PoolFirewallRule>();
        foreach (var rule in rules.OrderBy(r => r.Uid))
        {
            if (rule.Uid < uidFirst || rule.Uid > uidLast)
            {
                throw new ArgumentException("A rule's uid is outside the slot range.");
            }

            if (!seen.Add(rule.Uid))
            {
                throw new ArgumentException("A uid may have only one egress rule.");
            }

            if (!PoolRequestValidator.IsIpv4Literal(rule.ProxyHost))
            {
                throw new ArgumentException("A proxy host must be an IPv4 address literal; names are refused.");
            }

            if (rule.ProxyPort is < 1 or > 65535)
            {
                throw new ArgumentException("A proxy port must be 1 to 65535.");
            }

            ordered.Add(rule);
        }

        return ordered;
    }

    private static void AppendHeader(StringBuilder sb)
    {
        // Declaring the table first makes the delete succeed on a fresh VM; the whole file is one nft transaction.
        sb.Append("table inet ").Append(TableName).Append('\n');
        sb.Append("delete table inet ").Append(TableName).Append('\n');
        sb.Append("table inet ").Append(TableName).Append(" {\n");
    }

    private static void AppendOutputChain(StringBuilder sb, List<PoolFirewallRule> ordered, int uidFirst, int uidLast)
    {
        sb.Append("  chain output {\n");
        sb.Append("    type filter hook output priority 0; policy accept;\n");
        foreach (var rule in ordered)
        {
            sb.Append("    meta skuid ").Append(PoolRequestValidator.Invariant(rule.Uid))
              .Append(" ip daddr ").Append(rule.ProxyHost)
              .Append(" tcp dport ").Append(PoolRequestValidator.Invariant(rule.ProxyPort))
              .Append(" accept\n");
        }

        sb.Append("    meta skuid >= ").Append(PoolRequestValidator.Invariant(uidFirst))
          .Append(" meta skuid <= ").Append(PoolRequestValidator.Invariant(uidLast))
          .Append(" drop\n");
        sb.Append("  }\n");
    }

    private static void AppendVethChains(StringBuilder sb, List<PoolFirewallRule> ordered, BrowserPoolOptions options)
    {
        var wildcard = PoolNetwork.HostInterfacePrefix + "*";
        var slots = ordered.Select(r => (Rule: r, Slot: r.Uid - options.SlotUidBase)).ToList();

        sb.Append("  chain prerouting {\n");
        sb.Append("    type nat hook prerouting priority dstnat; policy accept;\n");
        foreach (var (rule, slot) in slots)
        {
            sb.Append("    iifname \"").Append(PoolNetwork.HostInterface(slot)).Append("\" ip daddr ").Append(PoolNetwork.HostAddress(options, slot))
              .Append(" tcp dport ").Append(PoolRequestValidator.Invariant(rule.ProxyPort))
              .Append(" dnat ip to ").Append(rule.ProxyHost).Append(':').Append(PoolRequestValidator.Invariant(rule.ProxyPort))
              .Append('\n');
        }

        sb.Append("  }\n");

        // A non-loopback endpoint is forwarded, so the endpoint must be able to answer an address it does not know.
        sb.Append("  chain postrouting {\n");
        sb.Append("    type nat hook postrouting priority srcnat; policy accept;\n");
        foreach (var (rule, slot) in slots.Where(s => !PoolNetwork.IsLoopback(s.Rule.ProxyHost)))
        {
            sb.Append("    ip saddr ").Append(PoolNetwork.SlotAddress(options, slot))
              .Append(" ip daddr ").Append(rule.ProxyHost)
              .Append(" tcp dport ").Append(PoolRequestValidator.Invariant(rule.ProxyPort))
              .Append(" masquerade\n");
        }

        sb.Append("  }\n");

        // input takes a DNAT to an address of this VM (loopback included), forward takes a DNAT to any other host. Each accepts
        // exactly the flow the prerouting rule rewrote, then everything else from any slot veth, active or stale, is dropped.
        foreach (var (chain, hook) in new[] { ("input", "input"), ("forward", "forward") })
        {
            sb.Append("  chain ").Append(chain).Append(" {\n");
            sb.Append("    type filter hook ").Append(hook).Append(" priority 0; policy accept;\n");
            foreach (var (rule, slot) in slots)
            {
                sb.Append("    iifname \"").Append(PoolNetwork.HostInterface(slot)).Append("\" ip daddr ").Append(rule.ProxyHost)
                  .Append(" tcp dport ").Append(PoolRequestValidator.Invariant(rule.ProxyPort))
                  .Append(" ct status dnat accept\n");
            }

            sb.Append("    iifname \"").Append(wildcard).Append("\" drop\n");
            sb.Append("  }\n");
        }

        // Nothing may start a connection into a slot from the VM's side or from another host.
        sb.Append("  chain forward_in {\n");
        sb.Append("    type filter hook forward priority 1; policy accept;\n");
        sb.Append("    oifname \"").Append(wildcard).Append("\" ct state new drop\n");
        sb.Append("  }\n");
    }
}
