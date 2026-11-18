using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Api;

/// <summary>
/// The two budgets that decide <c>unobserved</c> (doc 46 section 8.3). Both are measured on the client's own clock, from the time
/// the client received the last event and the last snapshot, so a skew between the server clock and the client clock does not
/// matter.
/// </summary>
/// <param name="Heartbeat">How long the stream may be silent before the display turns <c>unobserved</c>. Must be positive.</param>
/// <param name="Freshness">How old the last snapshot may be before the display turns <c>unobserved</c>. Must be positive.</param>
public sealed record KodoUnobservedBudgets(TimeSpan Heartbeat, TimeSpan Freshness)
{
    /// <summary>The shipped budgets: the 30 second heartbeat of section 8.3 and the PROPOSED 60 second freshness (section 15.6).</summary>
    public static KodoUnobservedBudgets Defaults { get; } = Create(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));

    /// <summary>Builds budgets, rejecting any that are not positive.</summary>
    public static KodoUnobservedBudgets Create(TimeSpan heartbeat, TimeSpan freshness)
    {
        if (heartbeat <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeat), heartbeat, "The heartbeat budget must be positive.");
        }

        if (freshness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshness), freshness, "The freshness budget must be positive.");
        }

        return new KodoUnobservedBudgets(heartbeat, freshness);
    }
}

/// <summary>
/// The client-side derivation of the display value (doc 46 section 8.3). Kodo cannot report its own death, so the client decides
/// <c>unobserved</c> from what it has received. This is a pure function: it reads nothing from the server, writes nothing to the
/// journal, and the result is a display value only, never a <see cref="KodoState"/>.
/// </summary>
public static class KodoUnobserved
{
    /// <summary>
    /// The value to show now.
    /// </summary>
    /// <remarks>
    /// Rules, all PROPOSED where section 8.3 leaves the edge open (doc 46 section 15.6):
    /// <list type="bullet">
    /// <item>No state has ever been received, or the stream has never been heard from, or no snapshot has ever arrived: <c>unobserved</c>.</item>
    /// <item>The stream is silent for longer than the heartbeat budget. Exactly the budget is still observed.</item>
    /// <item>The last snapshot is older than the freshness budget. Exactly the budget is still observed.</item>
    /// <item>A receive time later than <paramref name="now"/> (the client clock stepped back) counts as age zero.</item>
    /// <item>Otherwise the last known state is shown, and <c>frozen</c> stays <c>frozen</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="lastKnown">The state from the last snapshot or state change, or null when none was received.</param>
    /// <param name="now">The client's current time, in UTC.</param>
    /// <param name="lastEventAt">When the client last received any event on the <c>kodo</c> stream, or null.</param>
    /// <param name="lastSnapshotAt">When the client last received a snapshot, or null.</param>
    /// <param name="budgets">The heartbeat and freshness budgets.</param>
    public static KodoDisplayState Derive(
        KodoState? lastKnown,
        DateTimeOffset now,
        DateTimeOffset? lastEventAt,
        DateTimeOffset? lastSnapshotAt,
        KodoUnobservedBudgets budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);

        if (lastKnown is null || lastEventAt is null || lastSnapshotAt is null)
        {
            return KodoDisplayState.Unobserved;
        }

        if (Age(now, lastEventAt.Value) > budgets.Heartbeat)
        {
            return KodoDisplayState.Unobserved;
        }

        if (Age(now, lastSnapshotAt.Value) > budgets.Freshness)
        {
            return KodoDisplayState.Unobserved;
        }

        return DisplayOf(lastKnown.Value);
    }

    /// <summary>The display value for a server state, with no staleness applied.</summary>
    public static KodoDisplayState DisplayOf(KodoState state) => state switch
    {
        KodoState.Healthy => KodoDisplayState.Healthy,
        KodoState.Degraded => KodoDisplayState.Degraded,
        KodoState.Failing => KodoDisplayState.Failing,
        KodoState.Recovering => KodoDisplayState.Recovering,
        KodoState.Frozen => KodoDisplayState.Frozen,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a Kodo health state."),
    };

    private static TimeSpan Age(DateTimeOffset now, DateTimeOffset receivedAt)
    {
        TimeSpan age = now - receivedAt;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
