namespace Bazaar.Server.Kanban.Data;

/// <summary>Maps Kanban enums to the text values the 0012 CHECK constraints accept. Unknown text is an error, never a default.</summary>
internal static class KanbanText
{
    public static string ToSql(TaskState state) => state switch
    {
        TaskState.Backlog => "backlog",
        TaskState.Ready => "ready",
        TaskState.InProgress => "in_progress",
        TaskState.Review => "review",
        TaskState.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a stored Kanban state."),
    };

    public static TaskState ParseState(string text) => text switch
    {
        "backlog" => TaskState.Backlog,
        "ready" => TaskState.Ready,
        "in_progress" => TaskState.InProgress,
        "review" => TaskState.Review,
        "done" => TaskState.Done,
        _ => throw new InvalidOperationException($"Unknown task state '{text}'."),
    };

    public static string ToSql(TaskGate gate) => gate switch
    {
        TaskGate.None => "none",
        TaskGate.Council => "council",
        TaskGate.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, "Not a task gate."),
    };

    public static TaskGate ParseGate(string text) => text switch
    {
        "none" => TaskGate.None,
        "council" => TaskGate.Council,
        "owner" => TaskGate.Owner,
        _ => throw new InvalidOperationException($"Unknown task gate '{text}'."),
    };

    public static string ToSql(TaskLinkKind kind) => kind switch
    {
        TaskLinkKind.Pr => "pr",
        TaskLinkKind.Commit => "commit",
        TaskLinkKind.Gc => "gc",
        TaskLinkKind.VaultDoc => "vault_doc",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a task link kind."),
    };

    public static TaskLinkKind ParseLinkKind(string text) => text switch
    {
        "pr" => TaskLinkKind.Pr,
        "commit" => TaskLinkKind.Commit,
        "gc" => TaskLinkKind.Gc,
        "vault_doc" => TaskLinkKind.VaultDoc,
        _ => throw new InvalidOperationException($"Unknown task link kind '{text}'."),
    };
}
