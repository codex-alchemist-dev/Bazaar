namespace Bazaar.Server.Security.Quarantine;

/// <summary>
/// Quarantine (doc 15, "Quarantine"). Quarantining is open to any principal that detects a problem. Releasing is
/// owner-only. A quarantined agent has its tokens revoked and both VMs paused before the record is written, so a
/// failure leaves the agent running but unrecorded and a retry repeats the (idempotent) controls.
/// </summary>
public sealed class QuarantineService(IQuarantineStore store, IAgentTokenRevoker revoker, IVmPauser pauser)
{
    private readonly IQuarantineStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAgentTokenRevoker _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
    private readonly IVmPauser _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));

    /// <summary>
    /// Quarantines a subject. A subject already quarantined returns its active record and performs no side effects.
    /// Quarantining an agent revokes its tokens and pauses its VMs once.
    /// </summary>
    public async Task<QuarantineRecord> QuarantineAsync(
        QuarantineSubject subject,
        QuarantineReason reason,
        QuarantineActor actor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(actor);

        var active = await _store.GetActiveAsync(subject, ct).ConfigureAwait(false);
        if (active is not null)
        {
            return active;
        }

        if (subject.Kind == QuarantineSubjectKind.Agent)
        {
            await _revoker.RevokeAsync(subject.Id, ct).ConfigureAwait(false);
            await _pauser.PauseAsync(subject.Id, ct).ConfigureAwait(false);
        }

        var record = new QuarantineRecord(Guid.CreateVersion7(), subject, reason, actor, ReleasedBy: null);
        if (await _store.TryInsertActiveAsync(record, ct).ConfigureAwait(false))
        {
            return record;
        }

        // Another caller quarantined the same subject while this one was running its controls.
        return await _store.GetActiveAsync(subject, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Quarantine record vanished between insert and read.");
    }

    /// <summary>
    /// Releases a record. Only an owner may release; agents and readers are refused, and so is a record that is
    /// missing or already released. Release restores visibility for the subject only. It does not clear taint lineage
    /// and does not unpause VMs or restore tokens.
    /// </summary>
    public async Task<QuarantineDecision> ReleaseAsync(Guid recordId, QuarantineActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.Kind != QuarantineActorKind.Owner)
        {
            return QuarantineDecision.Refused("Only the owner can release a quarantine.");
        }

        var current = await _store.GetAsync(recordId, ct).ConfigureAwait(false);
        if (current is null)
        {
            return QuarantineDecision.Refused("No quarantine record with that id.");
        }

        if (current.Status == QuarantineStatus.Released)
        {
            return QuarantineDecision.Refused("The quarantine is already released.", current);
        }

        var released = await _store.TryReleaseAsync(recordId, actor, ct).ConfigureAwait(false);
        return released is null
            ? QuarantineDecision.Refused("The quarantine was released by another request.")
            : QuarantineDecision.Accepted(released);
    }
}
