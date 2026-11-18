namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// A sliding window over which an agent's token quota is counted (doc 48 section 11, doc 15 quota table).
/// Token budgets are quotas, not money budgets: no cost cap exists (DECIDED 11.2).
/// </summary>
public enum TokenBudgetWindow
{
    /// <summary>The trailing hour.</summary>
    Hour,

    /// <summary>The trailing 24 hours.</summary>
    Day,

    /// <summary>The trailing seven days.</summary>
    Week,
}

/// <summary>Length helpers for <see cref="TokenBudgetWindow"/>.</summary>
public static class TokenBudgetWindowExtensions
{
    /// <summary>The length of the sliding window.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    public static TimeSpan Length(this TokenBudgetWindow window) => window switch
    {
        TokenBudgetWindow.Hour => TimeSpan.FromHours(1),
        TokenBudgetWindow.Day => TimeSpan.FromDays(1),
        TokenBudgetWindow.Week => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown token budget window."),
    };
}
