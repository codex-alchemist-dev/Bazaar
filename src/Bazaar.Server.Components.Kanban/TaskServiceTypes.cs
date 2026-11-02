namespace Bazaar.Server.Kanban;

/// <summary>Who is making a change. <see cref="Id"/> is the agent or principal id, or <see cref="Guid.Empty"/> for the system.</summary>
public enum TaskActorKind
{
    System,
    Owner,
    Agent,
}

/// <summary>The actor recorded on every task event and used by the transition rules.</summary>
public readonly record struct TaskActor(TaskActorKind Kind, Guid Id)
{
    public static readonly TaskActor SystemActor = new(TaskActorKind.System, Guid.Empty);

    public string ToText() => Kind switch
    {
        TaskActorKind.System => "system",
        TaskActorKind.Owner => $"owner:{Id:D}",
        _ => $"agent:{Id:D}",
    };
}

/// <summary>Operator settings the task service reads (doc 12, Settings). Defaults are the shipped values.</summary>
public sealed record TaskServiceOptions
{
    /// <summary>kanban.auto_ready: Backlog dependents move to Ready when their last blocker is Done.</summary>
    public bool AutoReady { get; init; } = true;

    /// <summary>kanban.review_min_links, 0 to 5: PR or commit links needed for In progress to Review.</summary>
    public int ReviewMinLinks
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 5);
            field = value;
        }
    } = 1;

    /// <summary>kanban.reviewer_ungated_done: a reviewer may move an ungated task to Done.</summary>
    public bool ReviewerUngatedDone { get; init; } = true;

    /// <summary>kanban.max_blockers_per_task.</summary>
    public int MaxBlockersPerTask
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = BlockerGraph.DefaultMaxBlockersPerTask;
}

public enum TransitionStatus
{
    Applied,

    /// <summary>A guard or rule refused the move. Nothing changed (a refused start still records a guard event).</summary>
    Refused,

    /// <summary>The expected version was stale. Nothing changed.</summary>
    Conflict,

    /// <summary>The move is not in the table, or the task does not exist.</summary>
    Invalid,
}

/// <summary>Result of <see cref="TaskService.TransitionAsync"/>. <see cref="Version"/> is the new version when applied, or the current one on a conflict.</summary>
public sealed record TransitionResult(
    TransitionStatus Status,
    TransitionOutcome Outcome,
    long? Version,
    IReadOnlyList<Guid> UnfinishedBlockers)
{
    public static TransitionResult Applied(long newVersion) =>
        new(TransitionStatus.Applied, TransitionOutcome.Allowed, newVersion, []);

    public static TransitionResult Refused(TransitionOutcome outcome, IReadOnlyList<Guid> unfinishedBlockers) =>
        new(TransitionStatus.Refused, outcome, null, unfinishedBlockers);

    public static TransitionResult Conflict(long currentVersion) =>
        new(TransitionStatus.Conflict, TransitionOutcome.Allowed, currentVersion, []);

    public static TransitionResult Invalid(TransitionOutcome outcome) =>
        new(TransitionStatus.Invalid, outcome, null, []);
}

public enum EdgeStatus
{
    Added,
    Removed,
    NotPresent,
    NotFound,
    CrossProject,
    SelfEdge,
    Duplicate,
    Cycle,
    TooManyBlockers,
}

/// <summary>Result of an edge change. <see cref="CyclePath"/> is set for <see cref="EdgeStatus.Cycle"/>.</summary>
public sealed record EdgeResult(EdgeStatus Status, IReadOnlyList<Guid> CyclePath)
{
    public static EdgeResult Of(EdgeStatus status, IReadOnlyList<Guid>? cyclePath = null) => new(status, cyclePath ?? []);
}
