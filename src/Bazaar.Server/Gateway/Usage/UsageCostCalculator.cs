using System.Globalization;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Computes a call's cost on demand from its token counts and a price. Cost is never stored as truth.
/// </summary>
public static class UsageCostCalculator
{
    private const decimal _tokensPerMillion = 1_000_000m;

    /// <summary>
    /// The cost in USD, or null when the cost is unknown: there is no price, or a count is too large for <see cref="decimal"/>
    /// arithmetic (token counts are exact and unbounded; a wrong cost is never produced). <c>Thinking</c> is a subset of
    /// <c>Output</c>, so it is priced through <c>Output</c> only and never added again.
    /// </summary>
    public static decimal? Compute(GatewayTokenUsage tokens, PriceEntry? price)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (price is null)
        {
            return null;
        }

        try
        {
            if (!TryToDecimal(tokens.Input, out var input)
                || !TryToDecimal(tokens.CacheWrite5m, out var write5m)
                || !TryToDecimal(tokens.CacheWrite1h, out var write1h)
                || !TryToDecimal(tokens.CacheRead, out var read)
                || !TryToDecimal(tokens.Output, out var output))
            {
                return null;
            }

            var total = (input * price.InputPerMillion)
                + (write5m * price.CacheWrite5mRate)
                + (write1h * price.CacheWrite1hRate)
                + (read * price.CacheReadRate)
                + (output * price.OutputPerMillion);

            return total / _tokensPerMillion;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The count as a <see cref="decimal"/>, false when it is too large to hold. Goes through the canonical text, so no
    /// binary floating point is involved; a count with more than 28 decimal places is rounded, which a cost can bear.
    /// </summary>
    private static bool TryToDecimal(TokenAmount amount, out decimal value)
    {
        value = 0m;
        // decimal holds about 29 integer digits; refuse before building text for an amount that cannot fit.
        return amount.IntegerDigitCount <= 28
            && decimal.TryParse(amount.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
