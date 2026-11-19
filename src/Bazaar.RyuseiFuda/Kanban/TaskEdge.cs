namespace Bazaar.Server.Kanban;

/// <summary>One "<see cref="BlockerId"/> blocks <see cref="BlockedId"/>" dependency.</summary>
public readonly record struct TaskEdge(Guid BlockerId, Guid BlockedId);
