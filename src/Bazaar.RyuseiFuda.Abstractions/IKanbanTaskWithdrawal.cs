namespace Bazaar.RyuseiFuda.Kanban;

/// <summary>Result of <see cref="IKanbanTaskWithdrawal.WithdrawAsync"/>. Only <see cref="Withdrawn"/> changed anything.</summary>
public enum TaskWithdrawalStatus
{
    /// <summary>The task is now <see cref="TaskState.Withdrawn"/>.</summary>
    Withdrawn,

    /// <summary>The task was already withdrawn. Nothing changed, so a resumed import withdrawal can repeat the call.</summary>
    AlreadyWithdrawn,

    /// <summary>No task has this id. Nothing changed.</summary>
    UnknownTask,

    /// <summary>The actor may not withdraw this task: an agent, or a non-owner for a Done task. Nothing changed.</summary>
    NotAuthorised,

    /// <summary>No reason was given. Nothing changed.</summary>
    MissingReason,
}

/// <summary>
/// The narrow way into the stored <c>withdrawn</c> state (docs/design/12, "Withdrawn state"; docs/design/44 section 8.8).
/// An import withdrawal uses it to retract the tasks an import created. It keeps history, applies the doc 12 gates, and is
/// the only path to the state: a normal transition cannot reach it.
/// </summary>
public interface IKanbanTaskWithdrawal
{
    /// <summary>
    /// Withdraws one task. <paramref name="actor"/> must be the system or the owner, <paramref name="reason"/> is required
    /// (doc 44 uses <c>withdrawn by import</c>), and a Done task is withdrawn only by the owner.
    /// </summary>
    Task<TaskWithdrawalStatus> WithdrawAsync(Guid taskId, TaskActor actor, string reason, CancellationToken ct = default);
}
