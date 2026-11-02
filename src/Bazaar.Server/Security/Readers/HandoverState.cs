namespace Bazaar.Server.Security.Readers;

/// <summary>Where a hand-over stands. Released is reached only by an owner, and never makes it deliverable to actors.</summary>
public enum HandoverStatus
{
    /// <summary>Validated. Its fields may be given to an actor.</summary>
    Accepted,

    /// <summary>Failed validation. Held out of other contexts and out of promotion (doc 15).</summary>
    Quarantined,

    /// <summary>An owner lifted the quarantine hold. Still carries no fields an actor may use.</summary>
    Released,
}

/// <summary>Why a hand-over was quarantined. The payload itself stays in the audit store, not here.</summary>
/// <param name="HandoverId">The hand-over the record belongs to.</param>
/// <param name="Code">The validator's reason code.</param>
/// <param name="Detail">The validator's detail. Never contains the payload's text.</param>
public sealed record QuarantineRecord(Guid HandoverId, HandoverRejectCode Code, string Detail);

/// <summary>
/// The pure hand-over decision (doc 15). A validated payload becomes an accepted state; a refused payload becomes a
/// quarantined state with a reason. Only an <see cref="OwnerPrincipal"/> can release a quarantine, and there is no other
/// way to change the status. Immutable: <see cref="Release"/> returns a new value.
/// </summary>
public sealed class HandoverState
{
    private readonly AcceptedHandover? _accepted;

    private HandoverState(Guid handoverId, HandoverStatus status, AcceptedHandover? accepted, QuarantineRecord? quarantine)
    {
        HandoverId = handoverId;
        Status = status;
        _accepted = accepted;
        Quarantine = quarantine;
    }

    /// <summary>The hand-over's id.</summary>
    public Guid HandoverId { get; }

    /// <summary>Current status.</summary>
    public HandoverStatus Status { get; }

    /// <summary>The quarantine reason, present once the hand-over is quarantined or released.</summary>
    public QuarantineRecord? Quarantine { get; }

    /// <summary>
    /// The validated hand-over, only while <see cref="Status"/> is <see cref="HandoverStatus.Accepted"/>. Actors read
    /// hand-overs through this property, so a quarantined or released payload can never reach them.
    /// </summary>
    public AcceptedHandover? Accepted => Status == HandoverStatus.Accepted ? _accepted : null;

    /// <summary>Decides a hand-over from its validation result. Rejected payloads become quarantined with the reason.</summary>
    public static HandoverState Decide(Guid handoverId, HandoverResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result switch
        {
            HandoverResult.Accepted accepted => new HandoverState(handoverId, HandoverStatus.Accepted, accepted.Handover, null),
            HandoverResult.Rejected rejected => new HandoverState(
                handoverId,
                HandoverStatus.Quarantined,
                null,
                new QuarantineRecord(handoverId, rejected.Code, rejected.Detail)),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unknown validation result."),
        };
    }

    /// <summary>
    /// Lifts a quarantine hold after an owner has reviewed it. The quarantine record stays for audit, and the
    /// result has no accepted fields: an owner release clears the hold, it does not turn the payload into validated data.
    /// </summary>
    /// <param name="owner">The owner. Readers and actors cannot supply one.</param>
    /// <exception cref="InvalidOperationException">The hand-over is not quarantined.</exception>
    public HandoverState Release(OwnerPrincipal owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (Status != HandoverStatus.Quarantined)
        {
            throw new InvalidOperationException($"Only a quarantined hand-over can be released; this one is {Status}.");
        }

        return new HandoverState(HandoverId, HandoverStatus.Released, null, Quarantine);
    }
}
