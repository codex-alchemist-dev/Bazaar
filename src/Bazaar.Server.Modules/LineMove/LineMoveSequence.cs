namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// The step order as data. A step may run only when its predecessor has succeeded. The predecessor of
/// <see cref="LineMoveStep.RolledBack"/> is <see cref="LineMoveStep.Switch"/>: rollback is possible once the
/// switch has happened, whichever later step failed.
/// </summary>
public static class LineMoveSequence
{
    private static readonly LineMoveStep[] _forwardOrder =
    [
        LineMoveStep.Preflight,
        LineMoveStep.Snapshot,
        LineMoveStep.DryRun,
        LineMoveStep.Switch,
        LineMoveStep.Verify,
        LineMoveStep.Complete,
    ];

    /// <summary>The forward order, excluding the rollback step.</summary>
    public static IReadOnlyList<LineMoveStep> Forward => _forwardOrder;

    /// <summary>The step that must have succeeded before <paramref name="step"/> may run; null for Preflight.</summary>
    public static LineMoveStep? PredecessorOf(LineMoveStep step) => step switch
    {
        LineMoveStep.Preflight => null,
        LineMoveStep.RolledBack => LineMoveStep.Switch,
        _ => _forwardOrder[Array.IndexOf(_forwardOrder, step) - 1],
    };

    /// <summary>True when <paramref name="step"/> may run after <paramref name="lastSucceeded"/> (null when nothing has succeeded).</summary>
    public static bool CanRun(LineMoveStep step, LineMoveStep? lastSucceeded) =>
        PredecessorOf(step) == lastSucceeded;

    /// <summary>Throws <see cref="InvalidOperationException"/> when <paramref name="step"/> may not run yet.</summary>
    public static void EnsureCanRun(LineMoveStep step, LineMoveStep? lastSucceeded)
    {
        if (!CanRun(step, lastSucceeded))
        {
            throw new InvalidOperationException(
                $"Line move step {step} cannot run: its predecessor {PredecessorOf(step)?.ToString() ?? "(none)"} " +
                $"has not succeeded (last succeeded: {lastSucceeded?.ToString() ?? "(none)"}).");
        }
    }
}
