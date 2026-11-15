namespace Bazaar.Server.Agents.Turns.Tools.Db;

/// <summary>
/// Pure rules for the <c>in_flight_call</c> table (migration <c>turns_inflight/0001</c>). Kept free of I/O so the state
/// mapping and the lease length can be tested without PostgreSQL.
/// </summary>
public static class InFlightCallRows
{
    /// <summary>The call was begun and may have run. The CHECK list in the migration names the same two values.</summary>
    public const string Begun = "begun";

    /// <summary>The call returned, with a result or a converted failure.</summary>
    public const string Completed = "completed";

    /// <summary>
    /// How long a begun row keeps its lease. The lease lets a later run reclaim a row a crash left behind. It does not make a
    /// begun call finished, so <see cref="IsUnfinished"/> ignores it.
    /// </summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A begun call is unfinished regardless of its lease, because it may have run. A missing row (<c>null</c>) or a
    /// completed row is finished.
    /// </summary>
    public static bool IsUnfinished(string? state) => state == Begun;
}
