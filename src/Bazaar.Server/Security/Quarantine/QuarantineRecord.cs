namespace Bazaar.Server.Security.Quarantine;

/// <summary>Whether a subject is currently quarantined.</summary>
public enum QuarantineStatus
{
    /// <summary>Visible for audit, never delivered to a context or promoted.</summary>
    Quarantined = 0,

    /// <summary>An owner released the record. Visibility is restored; taint lineage is not cleared.</summary>
    Released = 1,
}

/// <summary>
/// One quarantine of one subject. Records are never deleted: a release sets <see cref="ReleasedBy"/> and keeps the
/// row, so the audit trail shows both steps.
/// </summary>
/// <param name="Id">Record identifier.</param>
/// <param name="Subject">What is quarantined.</param>
/// <param name="Reason">Why it was quarantined.</param>
/// <param name="CreatedBy">The principal that quarantined it.</param>
/// <param name="ReleasedBy">The owner that released it, or null while quarantined.</param>
public sealed record QuarantineRecord(
    Guid Id,
    QuarantineSubject Subject,
    QuarantineReason Reason,
    QuarantineActor CreatedBy,
    QuarantineActor? ReleasedBy)
{
    /// <summary>Quarantined until released, then released.</summary>
    public QuarantineStatus Status => ReleasedBy is null ? QuarantineStatus.Quarantined : QuarantineStatus.Released;
}
