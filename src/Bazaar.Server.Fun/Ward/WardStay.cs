namespace Bazaar.Server.Fun.Ward;

/// <summary>The check-in an admitted agent writes (doc 26, section 1). It is agent trust data and grants nothing.</summary>
/// <param name="Attempted">What the agent tried.</param>
/// <param name="BelievedFailing">What it believes is failing.</param>
/// <param name="Needed">What it needs.</param>
/// <param name="WillNotTouch">What it will leave alone.</param>
public sealed record WardCheckIn(string Attempted, string BelievedFailing, string Needed, string WillNotTouch);

/// <summary>
/// One stay in the ward. The failure record stays after discharge; <see cref="DischargedAt"/> is only set once.
/// </summary>
/// <param name="AgentId">The admitted agent.</param>
/// <param name="Trigger">What admitted the agent.</param>
/// <param name="AdmittedAt">When the stay began.</param>
/// <param name="CheckIn">The check-in, once written.</param>
/// <param name="DischargedAt">When the stay ended, or null while admitted.</param>
public sealed record WardStay(
    string AgentId,
    WardTrigger Trigger,
    DateTimeOffset AdmittedAt,
    WardCheckIn? CheckIn = null,
    DateTimeOffset? DischargedAt = null)
{
    /// <summary>True until discharge.</summary>
    public bool IsAdmitted => DischargedAt is null;
}

/// <summary>What an admitted agent may not do (doc 26: pushes, merges and public posts are refused, fixed, not a setting).</summary>
/// <param name="PushAllowed">Always false while admitted.</param>
/// <param name="MergeAllowed">Always false while admitted.</param>
/// <param name="PublicPostAllowed">Always false while admitted.</param>
/// <param name="InboxKept">Always true: the 09 cover takes the inbox and no items are dropped (doc 06).</param>
public sealed record WardRestrictions(bool PushAllowed, bool MergeAllowed, bool PublicPostAllowed, bool InboxKept)
{
    /// <summary>Restrictions for an admitted agent.</summary>
    public static WardRestrictions Admitted { get; } = new(false, false, false, true);

    /// <summary>No restriction: the agent is not in the ward.</summary>
    public static WardRestrictions None { get; } = new(true, true, true, true);
}
