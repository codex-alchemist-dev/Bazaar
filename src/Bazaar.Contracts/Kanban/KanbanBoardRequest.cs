namespace Bazaar.Contracts.Kanban;

/// <summary>Payload of the <c>board.snapshot</c> request: the project whose board the client wants.</summary>
/// <param name="ProjectId">Project id.</param>
public sealed record KanbanBoardRequest(Guid ProjectId);
