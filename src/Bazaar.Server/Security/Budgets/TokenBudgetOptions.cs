namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Operator settings for per-agent token quotas (doc 48 section 11.1, doc 15 quota table). Each value is the number of
/// tokens one agent may spend in that window.
/// </summary>
/// <remarks>
/// The shipped defaults are provisional: section 11.1 is OPEN, so these values are placeholders the operator can change in
/// settings. Every limit must be at least 1. Values are bound from configuration and checked by <see cref="Validate"/>.
/// </remarks>
public sealed class TokenBudgetOptions
{
    /// <summary>Tokens per agent per trailing hour. Provisional default.</summary>
    public long HourlyTokens { get; set; } = 200_000;

    /// <summary>Tokens per agent per trailing 24 hours. Provisional default.</summary>
    public long DailyTokens { get; set; } = 2_000_000;

    /// <summary>Tokens per agent per trailing seven days. Provisional default.</summary>
    public long WeeklyTokens { get; set; } = 10_000_000;

    /// <summary>The configured limit for one window.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    public long LimitFor(TokenBudgetWindow window) => window switch
    {
        TokenBudgetWindow.Hour => HourlyTokens,
        TokenBudgetWindow.Day => DailyTokens,
        TokenBudgetWindow.Week => WeeklyTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown token budget window."),
    };

    /// <summary>Checks that every window has a limit of at least 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A window limit is below 1.</exception>
    public void Validate()
    {
        if (HourlyTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(HourlyTokens), HourlyTokens, "The hourly token budget must be at least 1.");
        }

        if (DailyTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(DailyTokens), DailyTokens, "The daily token budget must be at least 1.");
        }

        if (WeeklyTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WeeklyTokens), WeeklyTokens, "The weekly token budget must be at least 1.");
        }
    }
}
