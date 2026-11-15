namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Sliding-window token quotas backed by an <see cref="ITokenBudgetStore"/>. On each call it reads the agent's spends
/// within the widest window (one week) from the store, decides, and appends only when the call is allowed. It keeps no
/// cache, so a new instance over the same store recovers every window. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Calls are serialised in this process, so concurrent calls never exceed a limit together. Platform-neutral: no OS-specific
/// APIs. Each call prunes the store before it reads, so a failure while pruning records nothing. The store, not this class,
/// makes the sequence safe across processes (see <see cref="ITokenBudgetStore"/>).
/// </remarks>
public sealed class TokenBudgetService : ITokenBudgetService, IDisposable
{
    private static readonly TokenBudgetWindow[] _checkOrder = [TokenBudgetWindow.Hour, TokenBudgetWindow.Day, TokenBudgetWindow.Week];

    private readonly TokenBudgetOptions _options;
    private readonly ITokenBudgetStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <exception cref="ArgumentNullException"><paramref name="options"/>, <paramref name="store"/> or <paramref name="time"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> has a window limit below 1.</exception>
    public TokenBudgetService(TokenBudgetOptions options, ITokenBudgetStore store, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<TokenBudgetDecision> TryConsume(string agentId, long tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ct.ThrowIfCancellationRequested();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var widest = now - TokenBudgetWindow.Week.Length();

            // Prune first: if this call fails here, nothing has been recorded yet.
            await _store.PruneOlderThan(widest, ct).ConfigureAwait(false);
            var spends = await _store.LoadSince(agentId, widest, ct).ConfigureAwait(false);

            TokenBudgetWindow? blocked = null;
            var blockedRetryAfter = TimeSpan.Zero;
            var tightest = long.MaxValue;

            // Every window is checked before anything is recorded, so a denial leaves no trace.
            foreach (var window in _checkOrder)
            {
                var limit = _options.LimitFor(window);
                var length = window.Length();
                var used = SumWithin(spends, now, length);
                tightest = Math.Min(tightest, Math.Max(0, limit - used));

                if (blocked is not null || used + tokens <= limit)
                {
                    continue;
                }

                blocked = window;
                blockedRetryAfter = RetryAfter(spends, now, length, used + tokens - limit, tokens > limit);
            }

            if (blocked is not null)
            {
                return new TokenBudgetDecision(Allowed: false, Window: blocked, Remaining: tightest, RetryAfter: blockedRetryAfter);
            }

            if (tokens > 0)
            {
                await _store.Append(agentId, now, tokens, ct).ConfigureAwait(false);
            }
            return new TokenBudgetDecision(Allowed: true, Window: null, Remaining: tightest - tokens, RetryAfter: TimeSpan.Zero);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the call gate. The service must not be used afterwards.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Tokens spent after the start of a window of <paramref name="length"/> ending at <paramref name="now"/>.</summary>
    private static long SumWithin(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TimeSpan length)
    {
        var cutoff = now - length;
        long total = 0;
        foreach (var spend in spends)
        {
            if (spend.At > cutoff)
            {
                total += spend.Tokens;
            }
        }
        return total;
    }

    /// <summary>
    /// Time until enough spend leaves the window to free <paramref name="needed"/> tokens. Spends leave oldest first, so the
    /// answer is the first spend whose departure frees enough room. A call larger than the whole limit never fits.
    /// </summary>
    private static TimeSpan RetryAfter(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TimeSpan length, long needed, bool neverFits)
    {
        if (neverFits)
        {
            return TimeSpan.MaxValue;
        }

        var cutoff = now - length;
        long freed = 0;
        foreach (var spend in spends)
        {
            if (spend.At <= cutoff)
            {
                continue;
            }
            freed += spend.Tokens;
            if (freed >= needed)
            {
                var wait = spend.At + length - now;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
        }
        return TimeSpan.MaxValue;
    }
}
