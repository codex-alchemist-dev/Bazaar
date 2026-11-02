namespace Bazaar.Server.Security.Quarantine;

/// <summary>
/// Storage for quarantine records. Records are append-only apart from the release field. Implementations must make
/// <see cref="TryInsertActiveAsync"/> and <see cref="TryReleaseAsync"/> atomic per subject or record.
/// </summary>
public interface IQuarantineStore
{
    /// <summary>The record with this id, or null.</summary>
    Task<QuarantineRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The unreleased record for this subject, or null when none is active.</summary>
    Task<QuarantineRecord?> GetActiveAsync(QuarantineSubject subject, CancellationToken ct = default);

    /// <summary>
    /// Inserts a record unless the subject already has an unreleased one. Returns false in that case, and nothing is
    /// written.
    /// </summary>
    Task<bool> TryInsertActiveAsync(QuarantineRecord record, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="QuarantineRecord.ReleasedBy"/> on an unreleased record and returns the updated record. Returns
    /// null when the record is missing or already released.
    /// </summary>
    Task<QuarantineRecord?> TryReleaseAsync(Guid id, QuarantineActor releasedBy, CancellationToken ct = default);
}
