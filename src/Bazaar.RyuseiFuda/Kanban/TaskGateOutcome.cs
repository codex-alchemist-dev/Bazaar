namespace Bazaar.Server.Kanban;

/// <summary>The result of a gate decision. Stored as <c>approved</c> or <c>rejected</c> (doc 12, Gates).</summary>
public enum TaskGateOutcome
{
    Approved,
    Rejected,
}
