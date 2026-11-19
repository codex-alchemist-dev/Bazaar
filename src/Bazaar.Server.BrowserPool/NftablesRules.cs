using System.Text;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// Renders the pool VM's nftables ruleset: each slot user may open TCP to its agent's proxy and nothing else, and every other
/// packet from any slot uid (loopback, UDP and DNS included) is dropped. The host applies the text as one atomic transaction.
/// </summary>
public static class NftablesRules
{
    /// <summary>The table name, so the host can inspect or remove it.</summary>
    public const string TableName = "bazaar_pool";

    /// <summary>Renders the ruleset for the active rules over the uid range <paramref name="uidFirst"/> to <paramref name="uidLast"/>.</summary>
    /// <exception cref="ArgumentException">A proxy host is not an IPv4 literal, a port or uid is out of range, or a uid has two rules.</exception>
    public static string Render(IEnumerable<PoolFirewallRule> rules, int uidFirst, int uidLast)
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

        var sb = new StringBuilder();
        // Declaring the table first makes the delete succeed on a fresh VM; the whole file is one nft transaction.
        sb.Append("table inet ").Append(TableName).Append('\n');
        sb.Append("delete table inet ").Append(TableName).Append('\n');
        sb.Append("table inet ").Append(TableName).Append(" {\n");
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
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Renders the ruleset for the pool's configured uid range.</summary>
    public static string Render(IEnumerable<PoolFirewallRule> rules, BrowserPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Render(rules, options.SlotUidBase, options.MaxUid);
    }
}
