namespace Bazaar.Server.Kanban;

/// <summary>Why a transition was allowed or refused. <see cref="NotATransition"/> means the table has no such move at all.</summary>
public enum TransitionOutcome
{
    Allowed,
    NotATransition,
    UnknownTask,
    NotAuthorised,
    MissingReason,
    NeedsLinks,
    PrNotMerged,
    GateNotSatisfied,
    BlockersUnfinished,
}

/// <summary>Facts the transition rules need. The task service fills these under the task's lock; the rules do no I/O.</summary>
public sealed record TransitionContext
{
    public bool IsSystem { get; init; }
    public bool IsOwner { get; init; }
    public bool IsAgent { get; init; }

    /// <summary>The acting agent is the task's <c>owner_agent_id</c>.</summary>
    public bool IsAssignee { get; init; }

    /// <summary>The owner, or an agent other than the assignee. Reviewer selection is not stored yet (see the PR notes).</summary>
    public bool IsReviewer { get; init; }
    public bool HasReason { get; init; }
    public int ReviewLinks { get; init; }
    public int MinReviewLinks { get; init; }
    public bool ReviewerUngatedDone { get; init; }
    public TaskGate Gate { get; init; }
    public bool GateSatisfied { get; init; }
    public bool HasPrLink { get; init; }

    /// <summary>A PR card is Done only when its PR is merged (doc 12, PR cards).</summary>
    public bool PrMerged { get; init; }
}

/// <summary>
/// The Kanban transition table (doc 12, Transition table), as pure code. Blocker guards are not here: the task service
/// checks them under lock, because they need the edge graph and the blockers' states.
/// </summary>
public static class TaskTransitionRules
{
    /// <summary>The nine moves the table allows, before any actor or guard check.</summary>
    public static bool IsListed(TaskState from, TaskState to) => (from, to) switch
    {
        (TaskState.Backlog, TaskState.Ready) => true,
        (TaskState.Ready, TaskState.InProgress) => true,
        (TaskState.InProgress, TaskState.Review) => true,
        (TaskState.Review, TaskState.InProgress) => true,
        (TaskState.Review, TaskState.Done) => true,
        (TaskState.Done, TaskState.Review) => true,
        (TaskState.Ready, TaskState.Backlog) => true,
        (TaskState.InProgress, TaskState.Backlog) => true,
        (TaskState.Review, TaskState.Backlog) => true,
        _ => false,
    };

    public static TransitionOutcome Evaluate(TaskState from, TaskState to, TransitionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!IsListed(from, to))
        {
            return TransitionOutcome.NotATransition;
        }

        switch (from, to)
        {
            case (TaskState.Backlog, TaskState.Ready):
                return ctx.IsSystem || ctx.IsOwner ? TransitionOutcome.Allowed : TransitionOutcome.NotAuthorised;

            case (TaskState.Ready, TaskState.InProgress):
                // The claim. The blocker guard runs in the service before this rule is consulted.
                return ctx.IsAgent || ctx.IsOwner ? TransitionOutcome.Allowed : TransitionOutcome.NotAuthorised;

            case (TaskState.InProgress, TaskState.Review):
                if (!ctx.IsAssignee)
                {
                    return TransitionOutcome.NotAuthorised;
                }

                return ctx.ReviewLinks < ctx.MinReviewLinks ? TransitionOutcome.NeedsLinks : TransitionOutcome.Allowed;

            case (TaskState.Review, TaskState.InProgress):
                if (!ctx.HasReason)
                {
                    return TransitionOutcome.MissingReason;
                }

                return ctx.IsReviewer || ctx.IsOwner ? TransitionOutcome.Allowed : TransitionOutcome.NotAuthorised;

            case (TaskState.Review, TaskState.Done):
                if (!(ctx.IsSystem || ctx.IsOwner || ctx.IsReviewer))
                {
                    return TransitionOutcome.NotAuthorised;
                }

                if (ctx.HasPrLink && !ctx.PrMerged)
                {
                    return TransitionOutcome.PrNotMerged;
                }

                if (ctx.Gate != TaskGate.None && !ctx.GateSatisfied)
                {
                    return TransitionOutcome.GateNotSatisfied;
                }

                if (ctx.Gate == TaskGate.None && !ctx.ReviewerUngatedDone && !(ctx.IsOwner || ctx.IsSystem))
                {
                    return TransitionOutcome.NotAuthorised;
                }

                return TransitionOutcome.Allowed;

            case (TaskState.Done, TaskState.Review):
                if (!ctx.HasReason)
                {
                    return TransitionOutcome.MissingReason;
                }

                return ctx.IsOwner ? TransitionOutcome.Allowed : TransitionOutcome.NotAuthorised;

            default:
                // Backward moves to Backlog from Ready, In progress or Review.
                if (!ctx.HasReason)
                {
                    return TransitionOutcome.MissingReason;
                }

                return ctx.IsOwner || ctx.IsAssignee ? TransitionOutcome.Allowed : TransitionOutcome.NotAuthorised;
        }
    }
}
