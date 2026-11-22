using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditAnomaly;

/// <summary>
/// Applies and undoes containments (doc 48 section 5.6). Agent and session containments are open to the engine and to
/// reviewers; a global freeze needs the owner role. Every containment, refusal and undo writes an audit row. Undo of a
/// containment still in force needs the owner's biometric check; anything but a clear yes refuses (fail closed).
/// </summary>
public sealed class ContainmentService(
    IContainmentPort port, IContainmentStore store, IBiometricCheck biometric, IAuditLog audit, AnomalySettings settings, TimeProvider clock)
{
    public async Task<ContainmentResult> ContainAsync(
        ContainmentTarget target, ContainmentActions actions, string reason, Requester requester, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(requester);
        var detail = AnomalyAudit.Describe(target, actions);
        detail["reason"] = reason;
        detail["requested_by"] = requester.PrincipalId;

        var invalid = actions == ContainmentActions.None
            || (target.Scope == ContainmentScope.Global ? target.Id is not null : string.IsNullOrEmpty(target.Id))
            || (target.Scope != ContainmentScope.Global && actions.HasFlag(ContainmentActions.FreezeWrites));
        if (invalid)
            return await RefuseAsync(AnomalyAuditKinds.ContainmentRefused, ContainmentOutcome.RefusedInvalid, "invalid", requester, detail, ct)
                .ConfigureAwait(false);
        if (target.Scope == ContainmentScope.Global && !requester.IsOwner)
            return await RefuseAsync(AnomalyAuditKinds.ContainmentRefused, ContainmentOutcome.RefusedNotOwner, "global needs owner", requester, detail, ct)
                .ConfigureAwait(false);

        try
        {
            await port.ApplyAsync(target, actions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail["error"] = ex.GetType().Name;
            await AnomalyAudit.WriteAsync(audit, clock, requester.PrincipalId, AnomalyAuditKinds.ContainmentFailed, detail, ct).ConfigureAwait(false);
            return new ContainmentResult(ContainmentOutcome.Failed, null);
        }

        var now = clock.GetUtcNow();
        var record = new ContainmentRecord(Guid.NewGuid(), target, actions, reason, requester.PrincipalId, now,
            now + settings.EffectiveUndoWindow, ContainmentState.InForce);
        await store.SaveAsync(record, ct).ConfigureAwait(false);
        detail["containment_id"] = record.Id.ToString();
        detail["undoable_until"] = record.UndoableUntil.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await AnomalyAudit.WriteAsync(audit, clock, requester.PrincipalId, AnomalyAuditKinds.ContainmentApplied, detail, ct).ConfigureAwait(false);
        return new ContainmentResult(ContainmentOutcome.Done, record);
    }

    public async Task<ContainmentResult> UndoAsync(Guid containmentId, Requester requester, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        var record = await store.FindAsync(containmentId, ct).ConfigureAwait(false);
        var detail = new JsonObject { ["containment_id"] = containmentId.ToString(), ["requested_by"] = requester.PrincipalId };
        if (record is null)
            return await RefuseAsync(AnomalyAuditKinds.UndoRefused, ContainmentOutcome.RefusedUnknown, "unknown containment", requester, detail, ct)
                .ConfigureAwait(false);
        foreach (var (k, v) in AnomalyAudit.Describe(record.Target, record.Actions))
            detail[k] = v?.DeepClone();

        if (record.State == ContainmentState.Undone)
            return await RefuseAsync(AnomalyAuditKinds.UndoRefused, ContainmentOutcome.RefusedAlreadyUndone, "already undone", requester, detail, ct, record)
                .ConfigureAwait(false);
        if (record.Target.Scope == ContainmentScope.Global && !requester.IsOwner)
            return await RefuseAsync(AnomalyAuditKinds.UndoRefused, ContainmentOutcome.RefusedNotOwner, "global needs owner", requester, detail, ct, record)
                .ConfigureAwait(false);
        if (clock.GetUtcNow() > record.UndoableUntil)
            return await RefuseAsync(AnomalyAuditKinds.UndoRefused, ContainmentOutcome.RefusedUndoWindowElapsed, "undo window elapsed", requester, detail, ct, record)
                .ConfigureAwait(false);

        // The containment is still in force, so undo needs the owner's biometric check. An error or a no is a refusal.
        var verified = false;
        try
        {
            verified = requester.IsOwner
                && await biometric.VerifyAsync(requester.PrincipalId, $"undo containment {record.Id}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail["biometric_error"] = ex.GetType().Name;
        }
        if (!requester.IsOwner || !verified)
            return await RefuseAsync(AnomalyAuditKinds.UndoRefused, ContainmentOutcome.RefusedBiometric,
                requester.IsOwner ? "biometric check failed" : "biometric needs owner", requester, detail, ct, record).ConfigureAwait(false);

        try
        {
            await port.RestoreAsync(record.Target, record.Actions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail["error"] = ex.GetType().Name;
            await AnomalyAudit.WriteAsync(audit, clock, requester.PrincipalId, AnomalyAuditKinds.ContainmentFailed, detail, ct).ConfigureAwait(false);
            return new ContainmentResult(ContainmentOutcome.Failed, record);
        }

        var undone = record with { State = ContainmentState.Undone };
        await store.SaveAsync(undone, ct).ConfigureAwait(false);
        await AnomalyAudit.WriteAsync(audit, clock, requester.PrincipalId, AnomalyAuditKinds.UndoApplied, detail, ct).ConfigureAwait(false);
        return new ContainmentResult(ContainmentOutcome.Done, undone);
    }

    private async Task<ContainmentResult> RefuseAsync(
        string kind, ContainmentOutcome outcome, string why, Requester requester, JsonObject detail, CancellationToken ct,
        ContainmentRecord? record = null)
    {
        detail["refused"] = why;
        await AnomalyAudit.WriteAsync(audit, clock, requester.PrincipalId, kind, detail, ct).ConfigureAwait(false);
        return new ContainmentResult(outcome, record);
    }
}
