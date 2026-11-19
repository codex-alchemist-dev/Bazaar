using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// One consumed token spend recorded for an agent.
/// </summary>
/// <param name="At">When the spend was recorded, as a UTC instant.</param>
/// <param name="Tokens">Tokens spent. Positive; a zero-token call is never recorded.</param>
public sealed record TokenBudgetSpend(DateTimeOffset At, TokenAmount Tokens);

/// <summary>
/// Durable (or in-memory) log of consumed token spends, the storage behind <see cref="ITokenBudgetService"/>. Counters
/// survive a restart because the service rebuilds every window from this log on each call; it keeps no cache of its own.
/// </summary>
/// <remarks>
/// Every instant is a UTC <see cref="DateTimeOffset"/>. <see cref="LoadSince"/> returns spends strictly after the given
/// instant, oldest first. <see cref="PruneOlderThan"/> removes spends at or before the given instant, since no sliding
/// window that starts there can count them. Implementations must be safe to call from several tasks at once.
/// <para>
/// <see cref="TryConsume"/> is the only operation the service uses to admit spend: it checks all three windows and records
/// the spend as one atomic unit, so no combination of tasks or processes sharing the store can overspend a window.
/// <see cref="Append"/> records unconditionally and exists for tests and tooling.
/// </para>
/// </remarks>
public interface ITokenBudgetStore
{
    /// <summary>Records one spend of <paramref name="tokens"/> for <paramref name="agentId"/> at <paramref name="at"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> cannot be stored by this store.</exception>
    Task Append(string agentId, DateTimeOffset at, TokenAmount tokens, CancellationToken ct = default);

    /// <summary>Returns the agent's spends strictly after <paramref name="since"/>, oldest first.</summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    Task<IReadOnlyList<TokenBudgetSpend>> LoadSince(string agentId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Removes every agent's spends at or before <paramref name="instant"/>. Spends after it are kept.</summary>
    Task PruneOlderThan(DateTimeOffset instant, CancellationToken ct = default);

    /// <summary>
    /// Atomically checks the hour, day and week windows ending at <paramref name="at"/> against <paramref name="limits"/> and,
    /// only if <paramref name="tokens"/> fit in all three, records the spend. A denied call records nothing. Concurrent calls
    /// for one agent, from any number of processes sharing the store, are serialised so admitted spend never exceeds a limit.
    /// A zero-token call is checked but never recorded.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    Task<TokenBudgetDecision> TryConsume(string agentId, DateTimeOffset at, TokenAmount tokens, TokenBudgetOptions limits, CancellationToken ct = default);
}
