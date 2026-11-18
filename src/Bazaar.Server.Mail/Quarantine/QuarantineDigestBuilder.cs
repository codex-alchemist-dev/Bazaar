namespace Bazaar.Server.Mail.Quarantine;

/// <summary>
/// Builds the daily owner digest (doc 22): counts per state and the top sender domains. It reads metadata only and keeps
/// nothing but the domain of a sender, so a digest never carries a body, a subject or a full address.
/// </summary>
public static class QuarantineDigestBuilder
{
    public const int DefaultTopDomains = 5;

    /// <summary>Counts every item by state; the top domains are ranked over held items, by count then name.</summary>
    public static QuarantineDigest Build(IEnumerable<QuarantineItem> items, DateOnly date, int topDomains = DefaultTopDomains)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(topDomains);
        var list = items.ToList();
        var top = list
            .Where(i => i.State == QuarantineState.Held)
            .Select(i => DomainOnly(i.SenderDomain))
            .Where(d => d.Length > 0)
            .GroupBy(d => d, StringComparer.Ordinal)
            .Select(g => new TopSenderDomain(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Domain, StringComparer.Ordinal)
            .Take(topDomains)
            .ToList();

        return new QuarantineDigest(
            date,
            Count(list, QuarantineState.Held),
            Count(list, QuarantineState.Released),
            Count(list, QuarantineState.Purged),
            Count(list, QuarantineState.Reported),
            top);
    }

    /// <summary>The agent-facing view: the held count and nothing else.</summary>
    public static QuarantineCountView CountView(int held) => new(Math.Max(0, held));

    private static int Count(List<QuarantineItem> items, QuarantineState state) => items.Count(i => i.State == state);

    // Defensive: if a caller put a full address in the field, keep only what follows the last '@'.
    private static string DomainOnly(string value)
    {
        var at = value.LastIndexOf('@');
        return (at >= 0 ? value[(at + 1)..] : value).Trim().ToLowerInvariant();
    }
}
