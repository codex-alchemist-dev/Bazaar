namespace Bazaar.Server.Kanban.Data;

/// <summary>Event kinds written to <c>task_events</c>; the list matches the 0012 CHECK constraint (doc 12, Events).</summary>
public static class TaskEventKinds
{
    public const string Created = "task.created";
    public const string Transitioned = "task.transitioned";
    public const string Blocked = "task.blocked";
    public const string Unblocked = "task.unblocked";
    public const string EdgeChanged = "task.edge_changed";
    public const string GuardRefused = "task.guard_refused";
    public const string GateDecided = "task.gate_decided";
    public const string DuePassed = "task.due_passed";
}

/// <summary>A row of <c>tasks</c>. <see cref="Version"/> is the optimistic-lock value the next write must match.</summary>
public sealed record TaskRecord(
    Guid Id,
    Guid ProjectId,
    string Title,
    TaskState State,
    Guid? OwnerAgentId,
    TaskGate Gate,
    Guid OriginGcId,
    Guid? OriginMessageId,
    DateTimeOffset? DueAt,
    Guid? ReviewApprovedBy,
    DateTimeOffset? ReviewApprovedAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Values for a new task. The caller supplies the id so tests and retries can know it before the insert.</summary>
public sealed record NewTask(
    Guid Id,
    Guid ProjectId,
    string Title,
    TaskState State,
    Guid? OwnerAgentId,
    TaskGate Gate,
    Guid OriginGcId,
    Guid? OriginMessageId,
    DateTimeOffset? DueAt);

/// <summary>A link from a task to something outside it (PR, commit, GC, Vault document).</summary>
public sealed record TaskLink(Guid TaskId, TaskLinkKind Kind, string Ref);

/// <summary>An event to append to <c>task_events</c>. The payload is JSON text, stored as jsonb.</summary>
public sealed record NewTaskEvent(Guid TaskId, string Kind, string Actor, string PayloadJson);

/// <summary>A row of <c>task_events</c>. <see cref="Seq"/> is assigned by the database.</summary>
public sealed record TaskEventRecord(
    long Seq,
    Guid TaskId,
    string Kind,
    string Actor,
    string PayloadJson,
    DateTimeOffset At);
