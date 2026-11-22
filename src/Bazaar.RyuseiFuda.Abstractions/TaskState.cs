namespace Bazaar.RyuseiFuda.Kanban;

// Layer 1 (macro board) of RyuseiFuda; see docs/design/38-ryusei-fuda.md.
/// <summary>
/// The stored Kanban states: the five columns, plus <see cref="Withdrawn"/>. Blocked is not a state: it is derived from edges.
/// </summary>
public enum TaskState
{
    Backlog,
    Ready,
    InProgress,
    Review,
    Done,

    /// <summary>
    /// Terminal. Set only by an import withdrawal through <see cref="IKanbanTaskWithdrawal"/> (docs/design/12, "Withdrawn state").
    /// Hidden from boards by default, never Blocked, and no longer counted as a blocker. Appended last so stored ordinals are unchanged.
    /// </summary>
    Withdrawn,
}
