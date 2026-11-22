namespace Bazaar.Server.PsychWard.Abstractions;

/// <summary>What admitted an agent to the Psych Ward (doc 26, section 1). A classifier hit alone is never a trigger.</summary>
public enum WardTrigger
{
    /// <summary>Any loop-guard trip (doc 06, C34).</summary>
    LoopGuard,

    /// <summary>Three consecutive failed outcomes on one tag (doc 11).</summary>
    RepeatedFailure,

    /// <summary>A denied call or canary anomaly (doc 15).</summary>
    DeniedCallOrCanary,

    /// <summary>A token hard stop from the shared limits (doc 15).</summary>
    TokenHardStop,

    /// <summary>The agent asked for admission itself.</summary>
    SelfReferral,

    /// <summary>The owner referred the agent.</summary>
    OwnerReferral,
}

/// <summary>The check-in an admitted agent writes. It is agent trust data and grants nothing.</summary>
/// <param name="Attempted">What the agent tried.</param>
/// <param name="BelievedFailing">What it believes is failing.</param>
/// <param name="Needed">What it needs.</param>
/// <param name="WillNotTouch">What it will leave alone.</param>
public sealed record WardCheckIn(string Attempted, string BelievedFailing, string Needed, string WillNotTouch);

/// <summary>One stay in the ward. The failure record stays after discharge.</summary>
/// <param name="Id">The stay id.</param>
/// <param name="AgentId">The admitted agent.</param>
/// <param name="Trigger">What admitted the agent.</param>
/// <param name="Detail">A short, secret-free note such as the loop-guard kind and reason.</param>
/// <param name="AdmittedAt">When the stay began.</param>
/// <param name="CheckIn">The check-in, once written.</param>
/// <param name="DischargedAt">When the stay ended; null while admitted.</param>
/// <param name="ReviewerId">Who discharged it, when a reviewer (not the owner) did.</param>
public sealed record WardStay(
    Guid Id,
    Guid AgentId,
    WardTrigger Trigger,
    string? Detail,
    DateTimeOffset AdmittedAt,
    WardCheckIn? CheckIn = null,
    DateTimeOffset? DischargedAt = null,
    Guid? ReviewerId = null)
{
    /// <summary>True until discharge.</summary>
    public bool IsAdmitted => DischargedAt is null;
}

/// <summary>The result of an admission, check-in or discharge request.</summary>
/// <param name="Accepted">True when the transition happened.</param>
/// <param name="Stay">The stay after the request, or null when the agent has none.</param>
/// <param name="Refusal">Why it was refused, or null when accepted.</param>
public sealed record WardOutcome(bool Accepted, WardStay? Stay, string? Refusal)
{
    /// <summary>A refused request.</summary>
    public static WardOutcome Refuse(string reason, WardStay? stay) => new(false, stay, reason);

    /// <summary>An accepted request.</summary>
    public static WardOutcome Accept(WardStay stay) => new(true, stay, null);
}

/// <summary>Who is asking to discharge a stay.</summary>
/// <param name="IsOwner">True when the owner discharges; the owner can always do so after the minimum stay.</param>
/// <param name="ReviewerId">The reviewing agent when it is not the owner.</param>
/// <param name="ReviewerIsClean">True when that reviewer has no tie to the failure and no taint.</param>
public sealed record DischargeRequest(bool IsOwner, Guid? ReviewerId = null, bool ReviewerIsClean = false);

/// <summary>What an agent may do given its ward state. Pushes, merges and public posts are refused while admitted (fixed).</summary>
/// <param name="PushAllowed">False while admitted.</param>
/// <param name="MergeAllowed">False while admitted.</param>
/// <param name="PublicPostAllowed">False while admitted.</param>
public sealed record WardRestrictions(bool PushAllowed, bool MergeAllowed, bool PublicPostAllowed)
{
    /// <summary>Restrictions for an admitted agent.</summary>
    public static WardRestrictions Admitted { get; } = new(false, false, false);

    /// <summary>No restriction.</summary>
    public static WardRestrictions None { get; } = new(true, true, true);
}

/// <summary>What the owner sees: current stays and vents waiting on a decision.</summary>
/// <param name="ActiveStays">Stays that are not discharged, newest first.</param>
/// <param name="HeldVents">Vent drafts held for the owner.</param>
public sealed record WardOverview(IReadOnlyList<WardStay> ActiveStays, IReadOnlyList<VentDraft> HeldVents);
