namespace Bazaar.Contracts.Kanban;

/// <summary>
/// One card on a Kanban board as the wire sends it. Ids are Guids (rendered as "D" strings by the JSON layer).
/// Blocked is not stored: a card is blocked while <see cref="UnfinishedBlockerIds"/> is not empty (doc 12).
/// </summary>
/// <param name="Id">Task id.</param>
/// <param name="Title">Task title.</param>
/// <param name="State">Stored column: "backlog", "ready", "in_progress", "review" or "done".</param>
/// <param name="OwnerAgentId">Accountable agent, or null when the task has no owner.</param>
/// <param name="UnfinishedBlockerIds">Prerequisites that are not Done, in edge order.</param>
/// <param name="BlockerTitles">Titles of the unfinished prerequisites, in the same order as <paramref name="UnfinishedBlockerIds"/>.</param>
public sealed record KanbanBoardCard(
    Guid Id,
    string Title,
    string State,
    Guid? OwnerAgentId,
    IReadOnlyList<Guid> UnfinishedBlockerIds,
    IReadOnlyList<string> BlockerTitles);

/// <summary>A whole project board: every task in the project with its blockers.</summary>
/// <param name="ProjectId">Project the board belongs to.</param>
/// <param name="Cards">Cards in the order the server stores them.</param>
public sealed record KanbanBoardSnapshot(Guid ProjectId, IReadOnlyList<KanbanBoardCard> Cards);
