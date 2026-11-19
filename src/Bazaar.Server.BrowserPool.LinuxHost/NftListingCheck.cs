using System.Globalization;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>
/// Reads the text of <c>nft list table</c> and decides whether the table and its egress rules are in force. It checks the facts that
/// make the pool safe (the output chain exists, a uid-range drop is in it, and each expected uid has its accept for its proxy), not
/// the whole text, so a different nft version's formatting does not break it. Anything it cannot find counts as missing.
/// </summary>
internal static class NftListingCheck
{
    /// <summary>Returns the problems found; empty when the listing is acceptable.</summary>
    /// <param name="listing">The stdout of <c>nft list table inet NAME</c>.</param>
    /// <param name="tableName">The table the listing is for.</param>
    /// <param name="expected">Rules whose accept line must be present, or empty to check only the table and the drop.</param>
    /// <param name="slotInterfacePrefix">The slot veth prefix whose drop must be in the input and forward chains, or null to skip that check.</param>
    public static IReadOnlyList<string> Problems(string listing, string tableName, IEnumerable<PoolFirewallRule> expected, string? slotInterfacePrefix = null)
    {
        var problems = new List<string>();
        var lines = listing.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        if (!lines.Any(l => l.StartsWith("table inet " + tableName + " ", StringComparison.Ordinal)))
        {
            problems.Add("the table is not present");
            return problems;
        }

        if (!lines.Any(l => l.StartsWith("chain output", StringComparison.Ordinal)) || !lines.Any(l => l.Contains("hook output", StringComparison.Ordinal)))
        {
            problems.Add("the output chain is not present");
        }

        if (!lines.Any(l => l.Contains("skuid", StringComparison.Ordinal) && l.EndsWith("drop", StringComparison.Ordinal) && !l.Contains("accept", StringComparison.Ordinal)))
        {
            problems.Add("the slot-uid drop rule is not present");
        }

        if (slotInterfacePrefix is not null)
        {
            // The renderer puts one such drop at the end of the input chain and one at the end of the forward chain.
            var vethDrop = "iifname \"" + slotInterfacePrefix + "*\" drop";
            if (lines.Count(l => string.Equals(l, vethDrop, StringComparison.Ordinal)) < 2)
            {
                problems.Add("the slot-interface drop rules (input and forward) are not present");
            }
        }

        foreach (var rule in expected)
        {
            var uid = rule.Uid.ToString(CultureInfo.InvariantCulture);
            var port = rule.ProxyPort.ToString(CultureInfo.InvariantCulture);
            var found = lines.Any(l =>
                l.EndsWith("accept", StringComparison.Ordinal)
                && HasWord(l, "skuid " + uid)
                && HasWord(l, "daddr " + rule.ProxyHost)
                && HasWord(l, "dport " + port));
            if (!found)
            {
                problems.Add("the accept rule for uid " + uid + " is not present");
            }
        }

        return problems;
    }

    private static bool HasWord(string line, string phrase)
    {
        var at = 0;
        while ((at = line.IndexOf(phrase, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + phrase.Length;
            var startOk = at == 0 || line[at - 1] == ' ';
            var endOk = end == line.Length || line[end] == ' ';
            if (startOk && endOk)
            {
                return true;
            }

            at = end;
        }

        return false;
    }
}
