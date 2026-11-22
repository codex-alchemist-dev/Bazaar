using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingPermissions;

/// <summary>How a command ended, as the runner saw it. The recorder maps it to a <see cref="ToolCallStatus"/>.</summary>
/// <param name="ExitCode">The process exit code, when there was one.</param>
/// <param name="TimedOut">True when the timeout killed the command. Never a success, whatever the exit code.</param>
/// <param name="Cancelled">True when the command was cancelled. Never a success, whatever the exit code.</param>
/// <param name="Crashed">True when the VM or guest crashed mid-command, so the result is not known.</param>
public sealed record ExecOutcome(int? ExitCode, bool TimedOut = false, bool Cancelled = false, bool Crashed = false);

/// <summary>The result of completing a call's trace row.</summary>
/// <param name="Status">The status the outcome maps to. <see cref="ToolCallStatus.Refused"/> for a call the gate refused.</param>
/// <param name="Recorded">True when this call wrote the status to the row.</param>
/// <param name="TraceFailed">True when the status should have been written but the store failed or refused. The status is still the true one; the row is the part that is stale.</param>
/// <param name="AlreadyCompleted">True when an earlier completion won; nothing was written by this call.</param>
public sealed record ToolCallCompletion(ToolCallStatus Status, bool Recorded, bool TraceFailed, bool AlreadyCompleted);

/// <summary>One gated call between the gate and its outcome.</summary>
public sealed class ToolCallScope
{
    private int _state;
    private ToolCallStatus _status;

    /// <summary>Creates a scope. Use <see cref="IToolCallRecorder.BeginAsync"/>.</summary>
    public ToolCallScope(ToolCallContext context, PermissionDecision decision, Guid? callId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        Context = context;
        Decision = decision;
        CallId = callId;
    }

    /// <summary>The call that was judged.</summary>
    public ToolCallContext Context { get; }

    /// <summary>The gate's decision.</summary>
    public PermissionDecision Decision { get; }

    /// <summary>The <c>tool_calls</c> row id; null when the gate wrote no row.</summary>
    public Guid? CallId { get; }

    /// <summary>True only when the gate allowed the call and wrote its row. Anything else must not run.</summary>
    public bool MayRun => Decision.Outcome == PermissionOutcome.Allow && CallId is not null;

    /// <summary>True once a completion has finished for this scope.</summary>
    public bool IsCompleted => Volatile.Read(ref _state) == 2;

    /// <summary>The status of the finished completion; meaningful only when <see cref="IsCompleted"/>.</summary>
    public ToolCallStatus CompletedStatus => _status;

    /// <summary>Claims the right to complete. False when another completion is running or done. For the recorder only.</summary>
    public bool TryBeginCompletion() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>Gives the claim back after a failed write so the caller can retry. For the recorder only.</summary>
    public void AbandonCompletion() => Volatile.Write(ref _state, 0);

    /// <summary>Marks the scope finished with <paramref name="status"/>. For the recorder only.</summary>
    public void FinishCompletion(ToolCallStatus status)
    {
        _status = status;
        Volatile.Write(ref _state, 2);
    }
}

/// <summary>Runs the permission gate and later completes the call's trace row with its outcome (doc 17, tables and edge cases).</summary>
public interface IToolCallRecorder
{
    /// <summary>Runs the gate, which writes the row, and returns a scope. Run the tool only when <see cref="ToolCallScope.MayRun"/> is true.</summary>
    ValueTask<ToolCallScope> BeginAsync(ToolCallContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Maps <paramref name="outcome"/> to a status and completes the row once. Success only on exit 0 and neither timed out nor
    /// cancelled; a timeout is <see cref="ToolCallStatus.TimedOut"/>, a cancel is failed, and a crash (or no exit code at all) leaves
    /// the row unknown. A refused scope never completes. A second call returns the first call's status and writes nothing.
    /// </summary>
    ValueTask<ToolCallCompletion> CompleteAsync(ToolCallScope scope, ExecOutcome outcome, CancellationToken cancellationToken);
}
