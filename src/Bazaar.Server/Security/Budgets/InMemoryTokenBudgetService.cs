namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Sliding-window token quotas held in process memory. Each agent keeps a log of (time, tokens) spends. Each call prunes
/// spends older than the widest window, then sums each window. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Counters are not shared across processes or restarts; a durable store is a separate concern. Platform-neutral: no
/// OS-specific APIs. Thread-safe: every call takes one lock, so concurrent calls never exceed a limit together.
/// </remarks>
public sealed class InMemoryTokenBudgetService : ITokenBudgetService
{
    private static readonly TokenBudgetWindow[] _checkOrder = [TokenBudgetWindow.Hour, TokenBudgetWindow.Day, TokenBudgetWindow.Week];

    private readonly TokenBudgetOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<Spend>> _spendByAgent = new(StringComparer.Ordinal);

    public InMemoryTokenBudgetService(TokenBudgetOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public Task<TokenBudgetDecision> TryConsume(string agentId, long tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var spends = SpendsFor(agentId);
            Prune(spends, now);

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
                return Task.FromResult(new TokenBudgetDecision(Allowed: false, Window: blocked, Remaining: tightest, RetryAfter: blockedRetryAfter));
            }

            if (tokens > 0)
            {
                spends.Enqueue(new Spend(now, tokens));
            }
            return Task.FromResult(new TokenBudgetDecision(Allowed: true, Window: null, Remaining: tightest - tokens, RetryAfter: TimeSpan.Zero));
        }
    }

    /// <summary>Returns the agent's spend log, creating it on first use. Caller holds <see cref="_gate"/>.</summary>
    private Queue<Spend> SpendsFor(string agentId)
    {
        if (!_spendByAgent.TryGetValue(agentId, out var spends))
        {
            spends = new Queue<Spend>();
            _spendByAgent.Add(agentId, spends);
        }
        return spends;
    }

    /// <summary>Drops spends that have left the widest window. Caller holds <see cref="_gate"/>.</summary>
    private static void Prune(Queue<Spend> spends, DateTimeOffset now)
    {
        var cutoff = now - TokenBudgetWindow.Week.Length();
        while (spends.Count > 0 && spends.Peek().At <= cutoff)
        {
            spends.Dequeue();
        }
    }

    /// <summary>Tokens spent after the start of a window of <paramref name="length"/> ending at <paramref name="now"/>.</summary>
    private static long SumWithin(Queue<Spend> spends, DateTimeOffset now, TimeSpan length)
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
    private static TimeSpan RetryAfter(Queue<Spend> spends, DateTimeOffset now, TimeSpan length, long needed, bool neverFits)
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

    private readonly record struct Spend(DateTimeOffset At, long Tokens);
}
