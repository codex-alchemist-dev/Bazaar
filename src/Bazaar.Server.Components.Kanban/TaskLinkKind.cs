namespace Bazaar.Server.Kanban;

/// <summary>Kinds of link a task may carry (doc 12, <c>task_links</c>).</summary>
public enum TaskLinkKind
{
    Pr,
    Commit,
    Gc,
    VaultDoc,
}
