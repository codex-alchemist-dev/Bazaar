namespace Bazaar.Server.Kanban;

// Layer 1 (macro board) of RyuseiFuda; see docs/design/38-ryusei-fuda.md.
/// <summary>The five stored Kanban columns. Blocked is not a state: it is derived from edges (see <see cref="BlockedStatus"/>).</summary>
public enum TaskState
{
    Backlog,
    Ready,
    InProgress,
    Review,
    Done,
}
