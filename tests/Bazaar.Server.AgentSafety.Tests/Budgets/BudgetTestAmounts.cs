using Bazaar.Contracts.Numerics;
using Bazaar.Server.Security.Budgets;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// Test shorthand for whole token counts now that budgets take <see cref="TokenAmount"/> (doc 48, DECIDED 11.3). Every
/// overload goes through <see cref="TokenAmount.FromInt64"/>, so a negative count still throws
/// <see cref="ArgumentOutOfRangeException"/> as the budget API did before.
/// </summary>
internal static class BudgetTestAmounts
{
    public static TokenAmount T(long tokens) => TokenAmount.FromInt64(tokens);

    public static TokenAmount T(string tokens) => TokenAmount.Parse(tokens);

    public static TokenBudgetOptions Limits(long hour, long day, long week) =>
        new() { HourlyTokens = T(hour), DailyTokens = T(day), WeeklyTokens = T(week) };

    public static TokenBudgetOptions Limits(string hour, string day, string week) =>
        new() { HourlyTokens = T(hour), DailyTokens = T(day), WeeklyTokens = T(week) };

    public static TokenAmount Sum(this IEnumerable<TokenBudgetSpend> spends, Func<TokenBudgetSpend, TokenAmount> selector) =>
        spends.Aggregate(TokenAmount.Zero, (total, spend) => total + selector(spend));

    public static Task<TokenBudgetDecision> TryConsume(this ITokenBudgetService service, string agentId, long tokens, CancellationToken ct = default) =>
        service.TryConsume(agentId, T(tokens), ct);

    public static Task Append(this ITokenBudgetStore store, string agentId, DateTimeOffset at, long tokens, CancellationToken ct = default) =>
        store.Append(agentId, at, T(tokens), ct);

    public static Task<TokenBudgetDecision> TryConsume(
        this ITokenBudgetStore store, string agentId, DateTimeOffset at, long tokens, TokenBudgetOptions limits, CancellationToken ct = default) =>
        store.TryConsume(agentId, at, T(tokens), limits, ct);
}
