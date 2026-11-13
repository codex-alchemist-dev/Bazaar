namespace Bazaar.Server.Modules.LineMove;

/// <summary>What the line move is for: the lines, who approved it, the rollback window and the preflight facts.</summary>
/// <param name="MoveId">Stable id for this move; carried on every audit event.</param>
/// <param name="FromLine">Current major line (for example 18).</param>
/// <param name="ToLine">Target major line (for example 19). Must be greater than <paramref name="FromLine"/>: downgrade is not supported.</param>
/// <param name="ApprovingRole">Role that approved the move, recorded on every audit event.</param>
/// <param name="RollbackWindowOpen">True when a Verify failure may roll back automatically (window set by doc 49).</param>
/// <param name="Preflight">Facts for the preflight rules.</param>
public sealed record LineMoveRequest(
    string MoveId,
    int FromLine,
    int ToLine,
    string ApprovingRole,
    bool RollbackWindowOpen,
    LineMovePreflightInputs Preflight);

/// <summary>How the run ended.</summary>
public enum LineMoveStatus
{
    /// <summary>Every step through Complete passed.</summary>
    Completed,

    /// <summary>A step failed before the switch (preflight, snapshot or dry run). Nothing live was changed.</summary>
    Failed,

    /// <summary>A failure after the switch was rolled back inside the automatic window.</summary>
    RolledBack,

    /// <summary>Rollback is needed but was not automatic: a failed switch, a closed window, or a rollback step that failed.</summary>
    ManualRollbackRequired,

    /// <summary>The run was cancelled. No success is claimed; see <see cref="LineMoveOutcome.RollbackRequired"/>.</summary>
    Cancelled,
}

/// <summary>The line's role after a move. Emitted as data; the runner never writes role storage.</summary>
public enum LineRole
{
    /// <summary>The newest line (the target of a move).</summary>
    Newest,

    /// <summary>The stable line (the line the move left).</summary>
    Stable,
}

/// <summary>A role change the caller applies to storage after a completed move.</summary>
public sealed record LineRoleAssignment(int Line, LineRole Role);

/// <summary>
/// The result of one run. <see cref="RoleAssignments"/> is non-empty only for <see cref="LineMoveStatus.Completed"/>,
/// which is reached only after Verify passed.
/// </summary>
/// <param name="Status">How the run ended.</param>
/// <param name="LastStep">The step where the run stopped (Complete for a completed run).</param>
/// <param name="SnapshotId">Snapshot id, or null when no snapshot was taken.</param>
/// <param name="RollbackRequired">True when the state may be partly switched and needs a rollback decision.</param>
/// <param name="RoleAssignments">Role changes to apply after a completed move.</param>
public sealed record LineMoveOutcome(
    LineMoveStatus Status,
    LineMoveStep LastStep,
    string? SnapshotId,
    bool RollbackRequired,
    IReadOnlyList<LineRoleAssignment> RoleAssignments);

/// <summary>
/// Runs the major line-move procedure (doc 42 section 10.3) as an ordered state machine over injected step executors.
/// Stops at the first failure, audits every step, and rolls back after a switch-stage failure only inside the window.
/// The runner touches no database and no process; the executors do.
/// </summary>
public sealed class LineMoveRunner
{
    private static readonly LineMoveStep[] ForwardAfterPreflight =
    [
        LineMoveStep.Snapshot,
        LineMoveStep.DryRun,
        LineMoveStep.Switch,
        LineMoveStep.Verify,
        LineMoveStep.Complete,
    ];

    private readonly Dictionary<LineMoveStep, ILineMoveStepExecutor> _executors;
    private readonly IAuditSink _audit;
    private readonly TimeProvider _time;

    /// <summary>Builds a runner. Every executor step (all but Preflight) must be supplied exactly once.</summary>
    public LineMoveRunner(IEnumerable<ILineMoveStepExecutor> executors, IAuditSink audit, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(audit);

        var map = new Dictionary<LineMoveStep, ILineMoveStepExecutor>();
        foreach (var executor in executors)
        {
            ArgumentNullException.ThrowIfNull(executor);
            if (executor.ForStep == LineMoveStep.Preflight)
                throw new ArgumentException("Preflight is a pure check, not an executor.", nameof(executors));
            if (!map.TryAdd(executor.ForStep, executor))
                throw new ArgumentException($"More than one executor for {executor.ForStep}.", nameof(executors));
        }

        foreach (var step in ForwardAfterPreflight.Append(LineMoveStep.RolledBack))
        {
            if (!map.ContainsKey(step))
                throw new ArgumentException($"No executor supplied for {step}.", nameof(executors));
        }

        _executors = map;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Runs the procedure for <paramref name="request"/> and returns its outcome.</summary>
    public async Task<LineMoveOutcome> RunAsync(LineMoveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var context = new LineMoveContext(request.MoveId, request.FromLine, request.ToLine, request.ApprovingRole, SnapshotId: null);
        LineMoveStep? lastSucceeded = null;

        if (cancellationToken.IsCancellationRequested)
            return await CancelledAsync(context, LineMoveStep.Preflight, rollbackRequired: false);

        // Preflight is a pure check, so the runner evaluates it directly and audits the result like any step.
        var preflight = LineMovePreflight.Evaluate(request.Preflight);
        if (!preflight.Passed)
        {
            var failed = string.Join(" ", preflight.Checks.Where(check => !check.Passed).Select(check => check.Detail));
            await AuditAsync(context, LineMoveStep.Preflight, LineMoveAuditKind.Failed, failed);
            return new LineMoveOutcome(LineMoveStatus.Failed, LineMoveStep.Preflight, null, false, []);
        }

        await AuditAsync(context, LineMoveStep.Preflight, LineMoveAuditKind.Succeeded, "All preflight rules passed.");
        lastSucceeded = LineMoveStep.Preflight;

        foreach (var step in ForwardAfterPreflight)
        {
            LineMoveSequence.EnsureCanRun(step, lastSucceeded);

            if (cancellationToken.IsCancellationRequested)
                return await CancelledAsync(context, step, RollbackRequiredAt(step));

            StepResult result;
            try
            {
                result = await _executors[step].ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await CancelledAsync(context, step, RollbackRequiredAt(step));
            }

            if (!result.Succeeded)
            {
                await AuditAsync(context, step, LineMoveAuditKind.Failed, result.Text);
                if (step == LineMoveStep.Verify)
                    return await RecoverAsync(request, context, lastSucceeded!.Value);

                // A failed switch may have stopped the old line half way. Automatic rollback is defined only for a Verify failure
                // (doc 42 section 10.3), so a failed switch is handed to the operator as a manual rollback decision.
                if (step == LineMoveStep.Switch)
                {
                    await AuditAsync(context, step, LineMoveAuditKind.ManualRollbackRequired, "Switch failed; the data state needs a manual rollback decision.");
                    return new LineMoveOutcome(LineMoveStatus.ManualRollbackRequired, step, context.SnapshotId, true, []);
                }

                return new LineMoveOutcome(LineMoveStatus.Failed, step, context.SnapshotId, false, []);
            }

            if (step == LineMoveStep.Snapshot)
            {
                if (string.IsNullOrWhiteSpace(result.SnapshotId))
                {
                    await AuditAsync(context, step, LineMoveAuditKind.Failed, "The snapshot step reported no snapshot id.");
                    return new LineMoveOutcome(LineMoveStatus.Failed, step, null, false, []);
                }

                context = context with { SnapshotId = result.SnapshotId };
            }

            await AuditAsync(context, step, LineMoveAuditKind.Succeeded, result.Text);
            lastSucceeded = step;
        }

        // Verify passed (it is the step before Complete), so the role changes are now data the caller may apply.
        return new LineMoveOutcome(
            LineMoveStatus.Completed,
            LineMoveStep.Complete,
            context.SnapshotId,
            RollbackRequired: false,
            RoleAssignments:
            [
                new LineRoleAssignment(request.ToLine, LineRole.Newest),
                new LineRoleAssignment(request.FromLine, LineRole.Stable),
            ]);
    }

    // Verify failed after a successful switch (lastSucceeded is Switch). Downgrade is not supported: rollback restores the
    // snapshot and starts the old line. It is automatic only inside the window.
    private async Task<LineMoveOutcome> RecoverAsync(LineMoveRequest request, LineMoveContext context, LineMoveStep lastSucceeded)
    {
        const LineMoveStep failedStep = LineMoveStep.Verify;
        if (!request.RollbackWindowOpen)
        {
            await AuditAsync(context, failedStep, LineMoveAuditKind.ManualRollbackRequired, "Rollback window is closed; manual runbook step required.");
            return new LineMoveOutcome(LineMoveStatus.ManualRollbackRequired, failedStep, context.SnapshotId, true, []);
        }

        LineMoveSequence.EnsureCanRun(LineMoveStep.RolledBack, lastSucceeded);
        // The rollback is not cancelled by the caller's token: a rollback that stops half way is worse than a late one.
        var rollback = await _executors[LineMoveStep.RolledBack].ExecuteAsync(context, CancellationToken.None);
        if (rollback.Succeeded)
        {
            await AuditAsync(context, LineMoveStep.RolledBack, LineMoveAuditKind.Succeeded, rollback.Text);
            return new LineMoveOutcome(LineMoveStatus.RolledBack, failedStep, context.SnapshotId, false, []);
        }

        await AuditAsync(context, LineMoveStep.RolledBack, LineMoveAuditKind.ManualRollbackRequired, rollback.Text);
        return new LineMoveOutcome(LineMoveStatus.ManualRollbackRequired, LineMoveStep.RolledBack, context.SnapshotId, true, []);
    }

    private async Task<LineMoveOutcome> CancelledAsync(LineMoveContext context, LineMoveStep step, bool rollbackRequired)
    {
        await AuditAsync(context, step, LineMoveAuditKind.Cancelled, "Cancelled before the step completed; no success is claimed.");
        return new LineMoveOutcome(LineMoveStatus.Cancelled, step, context.SnapshotId, rollbackRequired, []);
    }

    // A step at or after Switch may have changed the data directory or stopped the old line, so cancelling there needs a rollback decision.
    private static bool RollbackRequiredAt(LineMoveStep step) => step is LineMoveStep.Switch or LineMoveStep.Verify or LineMoveStep.Complete;

    private static void ValidateRequest(LineMoveRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MoveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApprovingRole);
        ArgumentNullException.ThrowIfNull(request.Preflight);
        if (request.ToLine <= request.FromLine)
            throw new ArgumentException("The target line must be newer than the current line: downgrade is not supported.", nameof(request));
    }

    private ValueTask AuditAsync(LineMoveContext context, LineMoveStep step, LineMoveAuditKind kind, string detail) =>
        _audit.RecordAsync(
            new LineMoveAuditEvent(context.MoveId, step, kind, context.FromLine, context.ToLine, context.ApprovingRole, context.SnapshotId, detail, _time.GetUtcNow()),
            CancellationToken.None);
}
