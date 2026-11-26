namespace Bazaar.Server.Subagents;

/// <summary>How much of a worktree a subagent gets (doc 17 "Review flow and subagents").</summary>
public enum WorktreeRequest
{
    /// <summary>No worktree; the child cannot touch files.</summary>
    None,

    /// <summary>A read-only view; the child never gets edit, exec or push tools.</summary>
    ReadOnly,

    /// <summary>A worktree of its own, never shared with another writer.</summary>
    Own,
}

/// <summary>What the parent asks the spawner to start. The child gets fresh context: only the task and explicit references.</summary>
/// <param name="ParentSessionId">The parent session; it must equal <see cref="ParentContext.ParentId"/>.</param>
/// <param name="Task">The task text. This is the whole context the child receives besides the references.</param>
/// <param name="ToolAllowlist">A closed set of tool names; it must be a subset of the parent's allowlist.</param>
/// <param name="ModelId">The requested model; it must be in the parent's whitelist.</param>
/// <param name="Worktree">The worktree request.</param>
/// <param name="DeclaredPaths">Repository-relative paths a writer declares it will touch (used when disjoint writers are on).</param>
/// <param name="References">Explicitly attached references (ids or paths); nothing else of the parent's context is passed.</param>
public sealed record SubagentSpec(
    string ParentSessionId,
    string Task,
    IReadOnlySet<string> ToolAllowlist,
    string ModelId,
    WorktreeRequest Worktree,
    IReadOnlyList<string>? DeclaredPaths = null,
    IReadOnlyList<string>? References = null);
