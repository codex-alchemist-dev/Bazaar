namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// Runs one line-move step against real storage or processes. The runner owns ordering, audit and outcomes;
/// an executor only performs its step and reports the result. Tests substitute fakes.
/// </summary>
public interface ILineMoveStepExecutor
{
    /// <summary>The step this executor performs (named ForStep because Step and Handles are reserved words for analyzers).</summary>
    LineMoveStep ForStep { get; }

    /// <summary>Performs the step. Throw <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<StepResult> ExecuteAsync(LineMoveContext context, CancellationToken cancellationToken);
}
