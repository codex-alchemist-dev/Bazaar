using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Token spend log held in process memory. The test fake and the default when no durable <see cref="ITokenBudgetStore"/>
/// is registered. Its contents are lost on restart, so an operator who needs quotas to survive one registers a durable store.
/// </summary>
/// <remarks>
/// Thread-safe: every call takes one lock. Platform-neutral: no OS-specific APIs.
/// </remarks>
public sealed class InMemoryTokenBudgetStore : ITokenBudgetStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<TokenBudgetSpend>> _spendsByAgent = new(StringComparer.Ordinal);

    public Task Append(string agentId, DateTimeOffset at, TokenAmount tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_spendsByAgent.TryGetValue(agentId, out var spends))
            {
                spends = [];
                _spendsByAgent.Add(agentId, spends);
            }
            spends.Add(new TokenBudgetSpend(at, tokens));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TokenBudgetSpend>> LoadSince(string agentId, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_spendsByAgent.TryGetValue(agentId, out var spends))
            {
                return Task.FromResult<IReadOnlyList<TokenBudgetSpend>>([]);
            }

            // Sorted on read, so a clock that steps backwards cannot break the oldest-first contract.
            var result = spends.Where(spend => spend.At > since).OrderBy(spend => spend.At).ToArray();
            return Task.FromResult<IReadOnlyList<TokenBudgetSpend>>(result);
        }
    }

    public Task PruneOlderThan(DateTimeOffset instant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            // Copy the keys first so agents with nothing left can be removed while walking the log.
            foreach (var agentId in _spendsByAgent.Keys.ToArray())
            {
                var spends = _spendsByAgent[agentId];
                spends.RemoveAll(spend => spend.At <= instant);
                if (spends.Count == 0)
                {
                    _spendsByAgent.Remove(agentId);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<TokenBudgetDecision> TryConsume(string agentId, DateTimeOffset at, TokenAmount tokens, TokenBudgetOptions limits, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _spendsByAgent.TryGetValue(agentId, out var spends);

            // Sorted so the retry-after walk sees spends leave oldest first, even if the clock stepped backwards.
            var ordered = spends is null ? [] : spends.OrderBy(spend => spend.At).ToArray();
            var decision = TokenBudgetEvaluator.Evaluate(ordered, at, tokens, limits);

            if (decision.Allowed && !tokens.IsZero)
            {
                if (spends is null)
                {
                    spends = [];
                    _spendsByAgent.Add(agentId, spends);
                }
                spends.Add(new TokenBudgetSpend(at, tokens));
            }
            return Task.FromResult(decision);
        }
    }
}
