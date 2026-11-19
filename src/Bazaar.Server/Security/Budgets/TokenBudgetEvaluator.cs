namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// The pure sliding-window arithmetic shared by every <see cref="ITokenBudgetStore.TryConsume"/> implementation, so the
/// in-memory and Postgres stores decide identically. Stateless and thread-safe.
/// </summary>
internal static class TokenBudgetEvaluator
{
    private static readonly TokenBudgetWindow[] _checkOrder = [TokenBudgetWindow.Hour, TokenBudgetWindow.Day, TokenBudgetWindow.Week];

    /// <summary>The earliest instant any window can still count: a spend at or before it is outside every window.</summary>
    public static DateTimeOffset WidestStart(DateTimeOffset now) => now - TokenBudgetWindow.Week.Length();

    /// <summary>The (exclusive) start of <paramref name="window"/> when it ends at <paramref name="now"/>.</summary>
    public static DateTimeOffset StartOf(TokenBudgetWindow window, DateTimeOffset now) => now - window.Length();

    /// <summary>
    /// Decides from the tokens already used in each window. The result has no retry time yet. When denied,
    /// <paramref name="needed"/> is how many tokens must leave the blocking window, and <paramref name="neverFits"/> is true
    /// when the call alone exceeds that window's limit.
    /// </summary>
    public static TokenBudgetDecision Decide(
        long hourUsed, long dayUsed, long weekUsed, long tokens, TokenBudgetOptions limits, out long needed, out bool neverFits)
    {
        TokenBudgetWindow? blocked = null;
        needed = 0;
        neverFits = false;
        var tightest = long.MaxValue;

        foreach (var window in _checkOrder)
        {
            var limit = limits.LimitFor(window);
            var used = window switch
            {
                TokenBudgetWindow.Hour => hourUsed,
                TokenBudgetWindow.Day => dayUsed,
                _ => weekUsed,
            };
            // Written as "tokens <= limit - used", never "used + tokens <= limit": a reported count near long.MaxValue
            // would wrap the sum negative and pass the check. Both operands of the subtraction are non-negative.
            var room = limit - used;
            tightest = Math.Min(tightest, Math.Max(0, room));

            if (blocked is not null || tokens <= room)
            {
                continue;
            }

            blocked = window;
            neverFits = tokens > limit;
            // used - (limit - tokens) is used + tokens - limit without the overflow. A call that never fits needs no count.
            needed = neverFits ? 0 : used - (limit - tokens);
        }

        return blocked is null
            ? new TokenBudgetDecision(Allowed: true, Window: null, Remaining: tightest - tokens, RetryAfter: TimeSpan.Zero)
            : new TokenBudgetDecision(Allowed: false, Window: blocked, Remaining: tightest, RetryAfter: TimeSpan.Zero);
    }

    /// <summary>Tokens spent after the start of <paramref name="window"/> ending at <paramref name="now"/>.</summary>
    public static long SumWithin(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TokenBudgetWindow window)
    {
        var cutoff = StartOf(window, now);
        long total = 0;
        foreach (var spend in spends)
        {
            if (spend.At > cutoff)
            {
                // Saturates instead of wrapping, so spends written around TryConsume (Append) can only over-count.
                total = spend.Tokens > long.MaxValue - total ? long.MaxValue : total + spend.Tokens;
            }
        }
        return total;
    }

    /// <summary>
    /// Time until enough spend leaves the window to free <paramref name="needed"/> tokens. Spends leave oldest first, so the
    /// answer is the first spend whose departure frees enough room. A call larger than the whole limit never fits.
    /// <paramref name="spends"/> must be oldest first.
    /// </summary>
    public static TimeSpan RetryAfter(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TokenBudgetWindow window, long needed, bool neverFits)
    {
        if (neverFits)
        {
            return TimeSpan.MaxValue;
        }

        var length = window.Length();
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

    /// <summary>Full decision over an in-hand spend list (oldest first), including the retry time on denial.</summary>
    public static TokenBudgetDecision Evaluate(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, long tokens, TokenBudgetOptions limits)
    {
        var decision = Decide(
            SumWithin(spends, now, TokenBudgetWindow.Hour),
            SumWithin(spends, now, TokenBudgetWindow.Day),
            SumWithin(spends, now, TokenBudgetWindow.Week),
            tokens, limits, out var needed, out var neverFits);
        return decision.Allowed
            ? decision
            : decision with { RetryAfter = RetryAfter(spends, now, decision.Window!.Value, needed, neverFits) };
    }
}
