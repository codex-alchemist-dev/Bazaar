namespace Bazaar.Contracts.Kodo;

/// <summary>
/// The budget that decides <c>unobserved</c> (doc 46 section 8.3). It is measured on the client's own clock, from the time the
/// client received the last event of any kind, so a skew between the server clock and the client clock does not matter.
/// </summary>
/// <param name="Heartbeat">How long the stream may be silent before the display turns <c>unobserved</c>. Must be positive.</param>
public sealed record KodoUnobservedBudgets(TimeSpan Heartbeat)
{
    /// <summary>The shipped budget: the 30 second heartbeat of section 8.3.</summary>
    public static KodoUnobservedBudgets Defaults { get; } = Create(TimeSpan.FromSeconds(30));

    /// <summary>Builds the budget, rejecting one that is not positive.</summary>
    public static KodoUnobservedBudgets Create(TimeSpan heartbeat)
    {
        if (heartbeat <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeat), heartbeat, "The heartbeat budget must be positive.");
        }

        return new KodoUnobservedBudgets(heartbeat);
    }
}

/// <summary>
/// The client-side derivation of the display value (doc 46 section 8.3). Kodo cannot report its own death, so the client decides
/// <c>unobserved</c> from what it has received. This is a pure function: it reads nothing from the server, writes nothing to the
/// journal, and the result is a display value only, never a server state.
/// </summary>
public static class KodoUnobserved
{
    /// <summary>
    /// The value to show now.
    /// </summary>
    /// <remarks>
    /// Rules, all PROPOSED where section 8.3 leaves the edge open (doc 46 section 15.6):
    /// <list type="bullet">
    /// <item>No state has ever been received, or no event has: <c>unobserved</c>.</item>
    /// <item>Every event refreshes freshness, whether it is a snapshot, a state change or a heartbeat. A snapshot arrives only on
    /// subscribe or after a gap, so a healthy connected client is kept observed by the heartbeats that prove nothing changed.</item>
    /// <item>The stream is silent for longer than the budget. Exactly the budget is still observed.</item>
    /// <item>A receive time later than <paramref name="now"/> (the client clock stepped back) counts as age zero.</item>
    /// <item>Otherwise the last known state is shown, and <c>frozen</c> stays <c>frozen</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="lastKnown">The state from the last snapshot or state change, or null when none was received. Never <see cref="KodoDisplayState.Unobserved"/>.</param>
    /// <param name="now">The client's current time, in UTC.</param>
    /// <param name="lastEventAt">When the client last received any event on the <c>kodo</c> stream, or null.</param>
    /// <param name="budgets">The silence budget.</param>
    public static KodoDisplayState Derive(
        KodoDisplayState? lastKnown,
        DateTimeOffset now,
        DateTimeOffset? lastEventAt,
        KodoUnobservedBudgets budgets)
    {
        ArgumentNullException.ThrowIfNull(budgets);

        if (lastKnown is KodoDisplayState.Unobserved)
        {
            throw new ArgumentOutOfRangeException(nameof(lastKnown), lastKnown, "Unobserved is derived, never a known server state.");
        }

        if (lastKnown is null || lastEventAt is null)
        {
            return KodoDisplayState.Unobserved;
        }

        TimeSpan age = now - lastEventAt.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age > budgets.Heartbeat ? KodoDisplayState.Unobserved : lastKnown.Value;
    }

    /// <summary>The display value for a wire state string, or false when it is not one of the five server states.</summary>
    public static bool TryDisplayOf(string? wire, out KodoDisplayState display)
    {
        switch (wire)
        {
            case "healthy": display = KodoDisplayState.Healthy; return true;
            case "degraded": display = KodoDisplayState.Degraded; return true;
            case "failing": display = KodoDisplayState.Failing; return true;
            case "recovering": display = KodoDisplayState.Recovering; return true;
            case "frozen": display = KodoDisplayState.Frozen; return true;
            default: display = default; return false;
        }
    }
}
