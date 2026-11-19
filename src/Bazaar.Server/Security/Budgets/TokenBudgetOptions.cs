using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Operator settings for per-agent token quotas (doc 48 section 11.1, doc 15 quota table). Each value is the number of
/// tokens one agent may spend in that window, as an exact <see cref="TokenAmount"/> (doc 48, DECIDED 11.3).
/// </summary>
/// <remarks>
/// The shipped defaults are provisional: section 11.1 is OPEN, so these values are placeholders the operator can change in
/// settings. Every limit must be greater than zero; fractions are allowed. Values are bound from configuration (text such as
/// <c>"500"</c>, <c>"2.5e6"</c> or <c>"0.5"</c>, within <see cref="TokenAmountBounds.PostgresNumeric"/>) and checked by
/// <see cref="Validate"/>.
/// </remarks>
public sealed class TokenBudgetOptions
{
    /// <summary>Tokens per agent per trailing hour. Provisional default.</summary>
    public TokenAmount HourlyTokens { get; set; } = TokenAmount.FromInt64(200_000);

    /// <summary>Tokens per agent per trailing 24 hours. Provisional default.</summary>
    public TokenAmount DailyTokens { get; set; } = TokenAmount.FromInt64(2_000_000);

    /// <summary>Tokens per agent per trailing seven days. Provisional default.</summary>
    public TokenAmount WeeklyTokens { get; set; } = TokenAmount.FromInt64(10_000_000);

    /// <summary>The configured limit for one window.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    public TokenAmount LimitFor(TokenBudgetWindow window) => window switch
    {
        TokenBudgetWindow.Hour => HourlyTokens,
        TokenBudgetWindow.Day => DailyTokens,
        TokenBudgetWindow.Week => WeeklyTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown token budget window."),
    };

    /// <summary>Checks that every window has a limit greater than zero.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A window limit is zero.</exception>
    public void Validate()
    {
        if (HourlyTokens.IsZero)
        {
            throw new ArgumentOutOfRangeException(nameof(HourlyTokens), HourlyTokens, "The hourly token budget must be greater than zero.");
        }

        if (DailyTokens.IsZero)
        {
            throw new ArgumentOutOfRangeException(nameof(DailyTokens), DailyTokens, "The daily token budget must be greater than zero.");
        }

        if (WeeklyTokens.IsZero)
        {
            throw new ArgumentOutOfRangeException(nameof(WeeklyTokens), WeeklyTokens, "The weekly token budget must be greater than zero.");
        }
    }
}
