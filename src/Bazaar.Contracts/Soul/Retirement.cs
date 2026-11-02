namespace Bazaar.Contracts.Soul;

/// <summary>
/// One step of a retirement, in the order the coordinator runs them (docs/design/07-soul-system.md, work package 6;
/// docs/design/14-identity-service-and-passport.md, "Kill switch"; docs/design/16-agent-vms-and-environments.md, "Retire").
/// </summary>
public enum RetirementStepKind
{
    /// <summary>Revoke the Soul's Passport entries.</summary>
    PassportRevocation,

    /// <summary>Revoke the agent's tokens and credential leases.</summary>
    TokenRevocation,

    /// <summary>Stop the agent's VM child disk.</summary>
    VmStop,

    /// <summary>Delete the VM child disk once its retention window has passed.</summary>
    VmChildDiskDelete,

    /// <summary>Delete a parent image disk. Refused while it still has other live children, or while the image is not retired.</summary>
    VmParentDiskDelete,
}

/// <summary>
/// The outcome of one retirement step. <see cref="Done"/> and <see cref="AlreadyDone"/> are successes; the rest are not.
/// </summary>
public enum RetirementStepStatus
{
    /// <summary>The step changed state in this run.</summary>
    Done,

    /// <summary>The step was already complete, from an earlier run or from the port finding nothing left to do.</summary>
    AlreadyDone,

    /// <summary>The step raised an error. <see cref="RetirementStepResult.Reason"/> says why. Later steps still run.</summary>
    Failed,

    /// <summary>The step is not due yet (the VM child disk is inside its retention window). Re-run to finish it.</summary>
    Deferred,

    /// <summary>The step was refused by a safety rule (for example, a parent disk that still has children). Nothing was changed.</summary>
    Refused,
}

/// <summary>One step's result, with a human-readable reason when the status is not a plain success.</summary>
/// <param name="Step">The step this result belongs to.</param>
/// <param name="Status">What happened.</param>
/// <param name="Reason">Why, for failed, deferred and refused steps; otherwise null.</param>
public sealed record RetirementStepResult(RetirementStepKind Step, RetirementStepStatus Status, string? Reason);

/// <summary>The result of one retirement run, steps in run order.</summary>
/// <param name="SoulId">The Soul being retired.</param>
/// <param name="AgentId">The agent being retired.</param>
/// <param name="Steps">One result per step that applied to this retirement, in run order.</param>
public sealed record RetirementReport(Guid SoulId, Guid AgentId, IReadOnlyList<RetirementStepResult> Steps)
{
    /// <summary>True when every step is done or already done. Deferred and refused steps make the run incomplete.</summary>
    public bool IsComplete => Steps.All(s => s.Status is RetirementStepStatus.Done or RetirementStepStatus.AlreadyDone);

    /// <summary>True when any step failed with an error. Other steps still ran.</summary>
    public bool HasFailures => Steps.Any(s => s.Status == RetirementStepStatus.Failed);
}
