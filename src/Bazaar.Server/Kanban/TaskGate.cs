namespace Bazaar.Server.Kanban;

/// <summary>What must approve a task before Review moves it to Done (doc 12, Gates). Stored as <c>none</c>, <c>council</c> or <c>owner</c>.</summary>
public enum TaskGate
{
    None,
    Council,
    Owner,
}
