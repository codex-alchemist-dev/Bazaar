namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// One consumed token spend recorded for an agent.
/// </summary>
/// <param name="At">When the spend was recorded, as a UTC instant.</param>
/// <param name="Tokens">Tokens spent. Positive; a zero-token call is never recorded.</param>
public sealed record TokenBudgetSpend(DateTimeOffset At, long Tokens);

/// <summary>
/// Durable (or in-memory) log of consumed token spends, the storage behind <see cref="ITokenBudgetService"/>. Counters
/// survive a restart because the service rebuilds every window from this log on each call; it keeps no cache of its own.
/// </summary>
/// <remarks>
/// Every instant is a UTC <see cref="DateTimeOffset"/>. <see cref="LoadSince"/> returns spends strictly after the given
/// instant, oldest first. <see cref="PruneOlderThan"/> removes spends at or before the given instant, since no sliding
/// window that starts there can count them. Implementations must be safe to call from several tasks at once.
/// <para>
/// <see cref="TokenBudgetService"/> serialises its own calls in one process. A store shared by several processes must
/// make the read-decide-append sequence safe itself, for example with a per-agent lock in the database.
/// </para>
/// </remarks>
public interface ITokenBudgetStore
{
    /// <summary>Records one spend of <paramref name="tokens"/> for <paramref name="agentId"/> at <paramref name="at"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokens"/> is negative.</exception>
    Task Append(string agentId, DateTimeOffset at, long tokens, CancellationToken ct = default);

    /// <summary>Returns the agent's spends strictly after <paramref name="since"/>, oldest first.</summary>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    Task<IReadOnlyList<TokenBudgetSpend>> LoadSince(string agentId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>Removes every agent's spends at or before <paramref name="instant"/>. Spends after it are kept.</summary>
    Task PruneOlderThan(DateTimeOffset instant, CancellationToken ct = default);
}
