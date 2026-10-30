namespace Bazaar.Client.Core.Kanban;

/// <summary>
/// One task card as the board shows it. Plain data, built without I/O; the wire layer fills it later
/// (docs/design/12-kanban-and-dependencies.md). Blocked is not stored: the board derives it from
/// <see cref="UnfinishedBlockerIds"/>.
/// </summary>
/// <param name="Id">Task id.</param>
/// <param name="Title">Task title.</param>
/// <param name="State">Stored column: "backlog", "ready", "in_progress", "review" or "done".</param>
/// <param name="OwnerAgentId">Accountable agent, or null when the task has no owner.</param>
/// <param name="UnfinishedBlockerIds">Prerequisites that are not Done. Any entry makes the card Blocked.</param>
/// <param name="BlockerTitles">Titles of the prerequisites, shown as blocker chips.</param>
public sealed record KanbanCard(
    Guid Id,
    string Title,
    string State,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> UnfinishedBlockerIds,
    IReadOnlyList<string> BlockerTitles);
