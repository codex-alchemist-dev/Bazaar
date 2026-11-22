namespace Bazaar.Server.Portability;

/// <summary>The stored Kanban columns in board order (doc 12). Blocked is not a column; it is derived (see <see cref="KanbanTaskRecord.UnfinishedBlockers"/>).</summary>
public enum KanbanColumn
{
    Backlog,
    Ready,
    InProgress,
    Review,
    Done,
}

/// <summary>What must approve a task before it moves to Done (doc 12, gates).</summary>
public enum KanbanGate
{
    None,
    Council,
    Owner,
}

/// <summary>Kinds of link a task may carry (doc 12, task links).</summary>
public enum KanbanLinkKind
{
    Pr,
    Commit,
    Gc,
    VaultDoc,
}

/// <summary>A link from a task to something outside it.</summary>
public sealed record KanbanLinkRecord(KanbanLinkKind Kind, string Ref);

/// <summary>
/// One task as the owner reads it. <see cref="UnfinishedBlockers"/> is the derived Blocked marking (doc 12, derived Blocked): empty
/// when the task is not blocked, otherwise the blockers that are not Done. The host computes it with the same rule the task
/// service uses; the exporter only writes it.
/// </summary>
public sealed record KanbanTaskRecord(
    Guid Id,
    Guid ProjectId,
    string Title,
    KanbanColumn Column,
    Guid? OwnerAgentId,
    KanbanGate Gate,
    Guid OriginGcId,
    Guid? OriginMessageId,
    DateTimeOffset? DueAt,
    Guid? ReviewApprovedBy,
    DateTimeOffset? ReviewApprovedAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Guid> UnfinishedBlockers,
    IReadOnlyList<KanbanLinkRecord> Links);

/// <summary>One "<see cref="BlockerId"/> blocks <see cref="BlockedId"/>" dependency (doc 12, task edges).</summary>
public sealed record KanbanEdgeRecord(Guid BlockerId, Guid BlockedId, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// One applied status transition (doc 12, task events). <see cref="Actor"/> is the text the task service records
/// (<c>system</c>, <c>owner:&lt;id&gt;</c> or <c>agent:&lt;id&gt;</c>). <see cref="Gate"/> is the gate that applied to the move.
/// </summary>
public sealed record KanbanTransitionRecord(
    long Seq,
    Guid TaskId,
    KanbanColumn From,
    KanbanColumn To,
    string Actor,
    DateTimeOffset At,
    string? Reason,
    KanbanGate Gate);

/// <summary>Which boards an export covers. Null <see cref="ProjectIds"/> means every board the caller may read.</summary>
public sealed record KanbanExportScope(IReadOnlyList<Guid>? ProjectIds = null)
{
    /// <summary>Every board.</summary>
    public static KanbanExportScope All { get; } = new();
}

/// <summary>
/// Reads Kanban data for the exporter. The host implements it over the task tables, for the caller's rights (doc 44 section 4.1:
/// the owner reads every board; doc 12 keeps board reads owner-only). Each read returns records in the order named here, and the
/// exporter refuses a source that breaks it, so the output bytes do not depend on the database's plan.
/// </summary>
public interface IKanbanExportSource
{
    /// <summary>Tasks in ascending id order (the canonical lowercase "D" text, ordinal).</summary>
    IAsyncEnumerable<KanbanTaskRecord> ReadTasksAsync(KanbanExportScope scope, CancellationToken ct);

    /// <summary>Edges in ascending order of blocker id, then blocked id (both as canonical "D" text, ordinal).</summary>
    IAsyncEnumerable<KanbanEdgeRecord> ReadEdgesAsync(KanbanExportScope scope, CancellationToken ct);

    /// <summary>Applied transitions in ascending sequence number.</summary>
    IAsyncEnumerable<KanbanTransitionRecord> ReadTransitionsAsync(KanbanExportScope scope, CancellationToken ct);
}
