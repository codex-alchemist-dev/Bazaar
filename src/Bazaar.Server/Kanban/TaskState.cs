namespace Bazaar.Server.Kanban;

/// <summary>The five stored Kanban columns. Blocked is not a state: it is derived from edges (see <see cref="BlockedStatus"/>).</summary>
public enum TaskState
{
    Backlog,
    Ready,
    InProgress,
    Review,
    Done,
}
