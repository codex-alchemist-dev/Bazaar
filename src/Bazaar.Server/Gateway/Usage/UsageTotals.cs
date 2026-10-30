using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Totals over a set of usage rows. <see cref="CostUsd"/> sums only the priced rows. A row with no price is counted in
/// <see cref="UnpricedCalls"/>, and the cost is then unknown, never zero (docs/design/23-observatory-and-usage.md).
/// </summary>
public sealed record UsageTotals(
    int Calls,
    GatewayTokenUsage Tokens,
    int ErrorCalls,
    int EstimatedCalls,
    int LongPromptCalls,
    decimal CostUsd,
    int UnpricedCalls)
{
    /// <summary>An empty set of totals.</summary>
    public static UsageTotals Empty { get; } = new(0, new GatewayTokenUsage(0, 0, 0, 0, 0, 0), 0, 0, 0, 0m, 0);

    /// <summary>True only when every call has a price, so <see cref="CostUsd"/> is the full cost.</summary>
    public bool IsCostKnown => UnpricedCalls == 0;
}

/// <summary>Builds <see cref="UsageTotals"/> for the usage view, with the identity split the dashboard shows.</summary>
public static class UsageTotalsBuilder
{
    /// <summary>The default prompt size, in tokens, above which a call is flagged. Mirrors <c>gateway.long_prompt.warn_tokens</c>.</summary>
    public const long DefaultLongPromptTokens = 100_000;

    /// <summary>
    /// Totals for the rows, plus one entry per identity present. Each row is priced at its own timestamp from
    /// <paramref name="prices"/>; with no table, every row is unpriced.
    /// </summary>
    public static (UsageTotals Total, IReadOnlyDictionary<GatewayIdentity, UsageTotals> ByIdentity) Build(
        IEnumerable<GatewayUsageRecord> records,
        PriceTable? prices,
        long longPromptTokens = DefaultLongPromptTokens)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfNegative(longPromptTokens);

        var total = new Totals();
        var byIdentity = new Dictionary<GatewayIdentity, Totals>();
        foreach (var record in records)
        {
            total.Add(record, prices, longPromptTokens);
            if (!byIdentity.TryGetValue(record.Identity, out var identityTotals))
            {
                identityTotals = new Totals();
                byIdentity[record.Identity] = identityTotals;
            }

            identityTotals.Add(record, prices, longPromptTokens);
        }

        var split = byIdentity.ToDictionary(pair => pair.Key, pair => pair.Value.ToTotals());
        return (total.ToTotals(), split);
    }

    private sealed class Totals
    {
        private int _calls;
        private int _errors;
        private int _estimated;
        private int _longPrompts;
        private int _unpriced;
        private decimal _cost;
        private GatewayTokenUsage _tokens = new(0, 0, 0, 0, 0, 0);

        public void Add(GatewayUsageRecord record, PriceTable? prices, long longPromptTokens)
        {
            _calls++;
            _tokens = TokenSums.Add(_tokens, record.Tokens);
            if (record.Status == UsageStatus.Error)
            {
                _errors++;
            }

            if (record.Estimated)
            {
                _estimated++;
            }

            if (TokenSums.PromptTokens(record.Tokens) > longPromptTokens)
            {
                _longPrompts++;
            }

            var entry = prices?.Lookup(record.ProviderId, record.ModelId, record.Timestamp);
            var cost = UsageCostCalculator.Compute(record.Tokens, entry);
            if (cost is { } value)
            {
                _cost += value;
            }
            else
            {
                _unpriced++;
            }
        }

        public UsageTotals ToTotals() => new(_calls, _tokens, _errors, _estimated, _longPrompts, _cost, _unpriced);
    }
}
