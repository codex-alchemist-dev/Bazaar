using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Computes a call's cost on demand from its token counts and a price. Cost is never stored as truth.
/// </summary>
public static class UsageCostCalculator
{
    private const decimal _tokensPerMillion = 1_000_000m;

    /// <summary>
    /// The cost in USD, or null when there is no price. <c>Thinking</c> is a subset of <c>Output</c>, so it is
    /// priced through <c>Output</c> only and never added again.
    /// </summary>
    public static decimal? Compute(GatewayTokenUsage tokens, PriceEntry? price)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (price is null)
        {
            return null;
        }

        var total = tokens.Input * price.InputPerMillion
            + tokens.CacheWrite5m * price.CacheWrite5mRate
            + tokens.CacheWrite1h * price.CacheWrite1hRate
            + tokens.CacheRead * price.CacheReadRate
            + tokens.Output * price.OutputPerMillion;

        return total / _tokensPerMillion;
    }
}
