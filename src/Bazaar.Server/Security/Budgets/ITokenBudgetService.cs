using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Outcome of one <see cref="ITokenBudgetService.TryConsume"/> call.
/// </summary>
/// <param name="Allowed">True when the tokens fit in every window and were recorded.</param>
/// <param name="Window">When denied, the first window (hour, then day, then week) that has no room. Null when allowed.</param>
/// <param name="Remaining">Tokens still available in the tightest window. After the spend when allowed; before it when denied.
/// Zero when the call was denied without counting (an amount with more integer digits than a limit, or a window sum that
/// cannot be represented).</param>
/// <param name="RetryAfter">When denied, the time until enough earlier spend leaves the blocking window. <see cref="TimeSpan.Zero"/>
/// when allowed. <see cref="TimeSpan.MaxValue"/> when the call alone is larger than the window limit and can never fit.</param>
public sealed record TokenBudgetDecision(bool Allowed, TokenBudgetWindow? Window, TokenAmount Remaining, TimeSpan RetryAfter);

/// <summary>
/// Token quota enforcement per agent (doc 48 section 11). A breach denies the call; the caller reports it. Cost never
/// gates work.
/// </summary>
public interface ITokenBudgetService
{
    /// <summary>
    /// Spends <paramref name="tokens"/> for <paramref name="agentId"/> if every window has room. All three windows are
    /// checked before anything is recorded, so a denied call records nothing.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <remarks>Amounts are exact and never wrap (doc 48, DECIDED 11.3). A zero amount is checked and never recorded.</remarks>
    Task<TokenBudgetDecision> TryConsume(string agentId, TokenAmount tokens, CancellationToken ct = default);
}
