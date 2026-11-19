using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// The pure sliding-window arithmetic shared by every <see cref="ITokenBudgetStore.TryConsume"/> implementation, so the
/// in-memory and Postgres stores decide identically. Stateless and thread-safe.
/// </summary>
/// <remarks>
/// Amounts are exact <see cref="TokenAmount"/> values, so no sum or difference can wrap (doc 48, DECIDED 11.3). A window
/// sum given as null is one the store could not represent; it counts as over every limit, so the call is denied.
/// </remarks>
public static class TokenBudgetEvaluator
{
    private static readonly TokenBudgetWindow[] _checkOrder = [TokenBudgetWindow.Hour, TokenBudgetWindow.Day, TokenBudgetWindow.Week];

    /// <summary>The earliest instant any window can still count: a spend at or before it is outside every window.</summary>
    public static DateTimeOffset WidestStart(DateTimeOffset now) => now - TokenBudgetWindow.Week.Length();

    /// <summary>The (exclusive) start of <paramref name="window"/> when it ends at <paramref name="now"/>.</summary>
    public static DateTimeOffset StartOf(TokenBudgetWindow window, DateTimeOffset now) => now - window.Length();

    /// <summary>
    /// The fast path, checked before anything is summed: a call with more integer digits than some window's limit can never
    /// fit that window. Returns that denial (the first such window in hour, day, week order, with
    /// <see cref="TimeSpan.MaxValue"/> and a remaining count of zero, since nothing was counted), or null when the call
    /// needs the full check. Costs only digit-count comparisons, so a hostile amount of any size is cheap to refuse.
    /// </summary>
    /// <exception cref="InvalidOperationException">A window limit is a reference; use the overload that takes the limits' digit counts.</exception>
    public static TokenBudgetDecision? RejectOversized(TokenAmount tokens, TokenBudgetOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return RejectOversized(tokens, window => limits.LimitFor(window).IntegerDigitCount);
    }

    /// <summary>
    /// The fast path over the limits' integer digit counts alone, for limits that are not held in memory (doc 48, DECIDED 11.4,
    /// "Limits as large amounts": the counts come from the stored headers).
    /// </summary>
    public static TokenBudgetDecision? RejectOversized(TokenAmount tokens, Func<TokenBudgetWindow, long> limitIntegerDigits)
    {
        ArgumentNullException.ThrowIfNull(limitIntegerDigits);
        foreach (var window in _checkOrder)
        {
            if (tokens.IntegerDigitCount > limitIntegerDigits(window))
            {
                return new TokenBudgetDecision(Allowed: false, Window: window, Remaining: TokenAmount.Zero, RetryAfter: TimeSpan.MaxValue);
            }
        }

        return null;
    }

    /// <summary>The denial for a call whose window sums could not be represented: over every limit, retry unknown.</summary>
    public static TokenBudgetDecision Unrepresentable() =>
        new(Allowed: false, Window: TokenBudgetWindow.Hour, Remaining: TokenAmount.Zero, RetryAfter: TimeSpan.MaxValue);

    /// <summary>
    /// Decides from the tokens already used in each window (null: not representable, over every limit). The result has no
    /// retry time yet. When denied, <paramref name="needed"/> is how many tokens must leave the blocking window (null when
    /// unknown), and <paramref name="neverFits"/> is true when the call alone exceeds that window's limit.
    /// </summary>
    public static TokenBudgetDecision Decide(
        TokenAmount? hourUsed, TokenAmount? dayUsed, TokenAmount? weekUsed, TokenAmount tokens, TokenBudgetOptions limits,
        out TokenAmount? needed, out bool neverFits)
    {
        TokenBudgetWindow? blocked = null;
        needed = null;
        neverFits = false;
        TokenAmount? tightest = null;

        foreach (var window in _checkOrder)
        {
            var limit = limits.LimitFor(window);
            var used = window switch
            {
                TokenBudgetWindow.Hour => hourUsed,
                TokenBudgetWindow.Day => dayUsed,
                _ => weekUsed,
            };

            // Room is limit - used, or zero when the window is already at or over its limit (or its sum is unknown).
            // A window over its limit has no room even for a zero-token call, as before amounts became exact.
            var room = TokenAmount.Zero;
            var withinLimit = used is { } known && TokenAmount.TrySubtract(limit, known, out room);
            tightest = tightest is { } current ? TokenAmount.Min(current, room) : room;

            if (blocked is not null || (withinLimit && tokens <= room))
            {
                continue;
            }

            blocked = window;
            neverFits = tokens > limit;
            needed = neverFits || used is not { } usedNow ? null : Needed(usedNow, limit, tokens, room);
        }

        var remaining = tightest ?? TokenAmount.Zero;
        if (blocked is null)
        {
            // tokens <= room in every window, so tokens <= the tightest room and the difference is never negative.
            TokenAmount.TrySubtract(remaining, tokens, out var after);
            return new TokenBudgetDecision(Allowed: true, Window: null, Remaining: after, RetryAfter: TimeSpan.Zero);
        }

        return new TokenBudgetDecision(Allowed: false, Window: blocked, Remaining: remaining, RetryAfter: TimeSpan.Zero);
    }

    /// <summary>used + tokens - limit, the spend that must leave the window, for a call that fits the limit on its own.</summary>
    private static TokenAmount Needed(TokenAmount used, TokenAmount limit, TokenAmount tokens, TokenAmount room)
    {
        if (TokenAmount.TrySubtract(used, limit, out var over))
        {
            // Over (or at) the limit already: everything above it, plus this call.
            return over + tokens;
        }

        // Under the limit: tokens > room = limit - used, so tokens - room is positive.
        TokenAmount.TrySubtract(tokens, room, out var shortfall);
        return shortfall;
    }

    /// <summary>Tokens spent after the start of <paramref name="window"/> ending at <paramref name="now"/>. Exact.</summary>
    public static TokenAmount SumWithin(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TokenBudgetWindow window)
    {
        var cutoff = StartOf(window, now);
        var total = TokenAmount.Zero;
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
    /// answer is the first spend whose departure frees enough room. A call larger than the whole limit never fits, and an
    /// unknown <paramref name="needed"/> gives <see cref="TimeSpan.MaxValue"/>. <paramref name="spends"/> must be oldest first.
    /// </summary>
    public static TimeSpan RetryAfter(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TokenBudgetWindow window, TokenAmount? needed, bool neverFits)
    {
        if (neverFits || needed is not { } target)
        {
            return TimeSpan.MaxValue;
        }

        var length = window.Length();
        var cutoff = now - length;
        var freed = TokenAmount.Zero;
        foreach (var spend in spends)
        {
            if (spend.At <= cutoff)
            {
                continue;
            }
            freed += spend.Tokens;
            if (freed >= target)
            {
                var wait = spend.At + length - now;
                return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            }
        }
        return TimeSpan.MaxValue;
    }

    /// <summary>
    /// Full decision over an in-hand spend list (oldest first), including the retry time on denial. A limit given by reference
    /// (doc 48, DECIDED 11.4) cannot be read here, so while one is set every call is denied as unrepresentable: fail closed.
    /// </summary>
    public static TokenBudgetDecision Evaluate(IReadOnlyList<TokenBudgetSpend> spends, DateTimeOffset now, TokenAmount tokens, TokenBudgetOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.HasLargeLimits)
        {
            return Unrepresentable();
        }

        if (RejectOversized(tokens, limits) is { } oversized)
        {
            return oversized;
        }

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
