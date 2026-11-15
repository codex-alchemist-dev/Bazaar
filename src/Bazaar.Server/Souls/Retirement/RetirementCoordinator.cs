using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Retirement;

/// <summary>
/// Runs the retirement steps for one Soul and agent in a fixed order (docs/design/07-soul-system.md, work package 6):
/// <list type="number">
/// <item>revoke Passport entries;</item>
/// <item>revoke tokens;</item>
/// <item>stop the VM child disk;</item>
/// <item>delete the VM child disk once its retention has passed;</item>
/// <item>delete a parent disk, only when it is retired and has no other live children.</item>
/// </list>
/// A step the ledger records as done is reported as already done and its port is not called again. A failing step is
/// recorded and the run goes on, so one broken revocation never blocks the rest. Soul data is never read or written here:
/// the Soul's version chain is kept (docs/design/16, "Retire").
/// </summary>
// TODO(component): move to AddBazaarSouls() when the component model (PR #125) merges.
// The agent repository exposes the post-transition observer seam; a host adapter can invoke this coordinator from it once
// production Passport, token and VM ports are available.
public sealed class RetirementCoordinator(
    IPassportRevoker passports,
    ITokenRevoker tokens,
    IVmDiskRetirer disks,
    IRetirementLedger ledger,
    TimeProvider clock)
{
    /// <summary>Runs every applicable step once, or reports what a previous run already did. Never throws for a step failure.</summary>
    public async Task<RetirementReport> RetireAsync(RetirementRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.Subject;
        var completed = (await ledger.ReadAsync(subject, cancellationToken).ConfigureAwait(false))
            .Where(r => r.Status is RetirementStepStatus.Done or RetirementStepStatus.AlreadyDone)
            .Select(r => r.Step)
            .ToHashSet();

        var steps = new List<RetirementStepResult>
        {
            await RunAsync(subject, RetirementStepKind.PassportRevocation, completed, null,
                () => passports.RevokeAsync(subject, cancellationToken), cancellationToken).ConfigureAwait(false),
            await RunAsync(subject, RetirementStepKind.TokenRevocation, completed, null,
                () => tokens.RevokeAsync(subject, cancellationToken), cancellationToken).ConfigureAwait(false),
        };

        if (request.ChildDisk is { } child)
        {
            steps.Add(await RunAsync(subject, RetirementStepKind.VmStop, completed, null,
                () => disks.StopAsync(child, cancellationToken), cancellationToken).ConfigureAwait(false));

            var childGate = clock.GetUtcNow() < child.RetainUntil
                ? new RetirementStepResult(
                    RetirementStepKind.VmChildDiskDelete,
                    RetirementStepStatus.Deferred,
                    $"Retention runs until {child.RetainUntil:O}.")
                : null;
            steps.Add(await RunAsync(subject, RetirementStepKind.VmChildDiskDelete, completed, childGate,
                () => disks.DeleteChildDiskAsync(child, cancellationToken), cancellationToken).ConfigureAwait(false));
        }

        if (request.ParentDisk is { } parent)
        {
            var parentGate = ParentGate(parent);
            steps.Add(await RunAsync(subject, RetirementStepKind.VmParentDiskDelete, completed, parentGate,
                () => disks.DeleteParentDiskAsync(parent, cancellationToken), cancellationToken).ConfigureAwait(false));
        }

        return new RetirementReport(subject.SoulId, subject.AgentId, steps);
    }

    /// <summary>A parent disk is never deleted while another child uses it, or before its image is retired.</summary>
    private static RetirementStepResult? ParentGate(VmParentDisk parent)
    {
        if (parent.OtherLiveChildren > 0)
            return new RetirementStepResult(
                RetirementStepKind.VmParentDiskDelete,
                RetirementStepStatus.Refused,
                $"Parent image {parent.ImageId} still has {parent.OtherLiveChildren} live child disk(s).");
        if (!parent.ImageRetired)
            return new RetirementStepResult(
                RetirementStepKind.VmParentDiskDelete,
                RetirementStepStatus.Refused,
                $"Parent image {parent.ImageId} is not retired.");
        return null;
    }

    /// <summary>
    /// Runs one step. A step already completed in an earlier run is reported as already done without calling its port.
    /// A non-null <paramref name="gate"/> stops the step before its port runs. The outcome is recorded either way.
    /// </summary>
    private async Task<RetirementStepResult> RunAsync(
        SoulRetirementSubject subject,
        RetirementStepKind kind,
        HashSet<RetirementStepKind> completed,
        RetirementStepResult? gate,
        Func<Task<RetirementStepStatus>> action,
        CancellationToken cancellationToken)
    {
        if (completed.Contains(kind))
            return new RetirementStepResult(kind, RetirementStepStatus.AlreadyDone, "Completed in an earlier run.");

        var result = gate ?? await ExecuteAsync(kind, action, cancellationToken).ConfigureAwait(false);
        await ledger.RecordAsync(subject, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Calls a port. A thrown error becomes a failed result. Cancellation still propagates.</summary>
    private static async Task<RetirementStepResult> ExecuteAsync(
        RetirementStepKind kind,
        Func<Task<RetirementStepStatus>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await action().ConfigureAwait(false);
            return status is RetirementStepStatus.Done or RetirementStepStatus.AlreadyDone
                ? new RetirementStepResult(kind, status, null)
                : new RetirementStepResult(kind, RetirementStepStatus.Failed, $"Port reported {status}, not a success.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new RetirementStepResult(kind, RetirementStepStatus.Failed, ex.Message);
        }
    }
}
