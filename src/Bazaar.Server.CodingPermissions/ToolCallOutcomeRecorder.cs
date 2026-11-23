using Bazaar.Contracts.Coding;

namespace Bazaar.Server.CodingPermissions;

/// <summary>
/// The default <see cref="IToolCallRecorder"/>: begins a call through the gate and completes its <c>tool_calls</c> row exactly
/// once through <see cref="IToolCallTrace.CompleteAsync"/>.
/// </summary>
public sealed class ToolCallOutcomeRecorder : IToolCallRecorder
{
    private readonly IIdentifiedToolPermissionGate _gate;
    private readonly IToolCallTrace _trace;

    /// <summary>Creates the recorder.</summary>
    public ToolCallOutcomeRecorder(IIdentifiedToolPermissionGate gate, IToolCallTrace trace)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(trace);
        _gate = gate;
        _trace = trace;
    }

    /// <summary>The status an outcome maps to; <see cref="ToolCallStatus.Unknown"/> means the row is left open.</summary>
    public static ToolCallStatus Map(ExecOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Crashed)
        {
            return ToolCallStatus.Unknown;
        }

        if (outcome.TimedOut)
        {
            return ToolCallStatus.TimedOut;
        }

        if (outcome.Cancelled)
        {
            return ToolCallStatus.Failed;
        }

        return outcome.ExitCode switch
        {
            0 => ToolCallStatus.Succeeded,
            null => ToolCallStatus.Unknown,
            _ => ToolCallStatus.Failed,
        };
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallScope> BeginAsync(ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var evaluation = await _gate.EvaluateWithIdAsync(context, cancellationToken).ConfigureAwait(false);
        return new ToolCallScope(context, evaluation.Decision, evaluation.CallId);
    }

    /// <inheritdoc />
    public async ValueTask<ToolCallCompletion> CompleteAsync(ToolCallScope scope, ExecOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(outcome);

        // The gate already wrote a refused row; a call that never ran has no outcome to add and is never a success.
        if (!scope.MayRun)
        {
            return new ToolCallCompletion(ToolCallStatus.Refused, false, false, false);
        }

        if (!scope.TryBeginCompletion())
        {
            return new ToolCallCompletion(scope.IsCompleted ? scope.CompletedStatus : Map(outcome), false, false, true);
        }

        var status = Map(outcome);
        if (status == ToolCallStatus.Unknown)
        {
            // Unknown is not an outcome the table stores: the row stays as the gate wrote it.
            scope.FinishCompletion(status);
            return new ToolCallCompletion(status, false, false, false);
        }

        bool written;
        try
        {
            written = await _trace.CompleteAsync(scope.CallId!.Value, status, outcome.ExitCode, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            scope.AbandonCompletion();
            throw;
        }
        catch (Exception)
        {
            // The status is true; only the row is stale. Surface it and let the caller retry.
            scope.AbandonCompletion();
            return new ToolCallCompletion(status, false, true, false);
        }

        if (!written)
        {
            scope.AbandonCompletion();
            return new ToolCallCompletion(status, false, true, false);
        }

        scope.FinishCompletion(status);
        return new ToolCallCompletion(status, true, false, false);
    }
}
