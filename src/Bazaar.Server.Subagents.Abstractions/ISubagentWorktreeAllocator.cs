namespace Bazaar.Server.Subagents;

/// <summary>A worktree handed to a child.</summary>
/// <param name="Id">The lease id.</param>
/// <param name="Path">The worktree path; one writer per path, always.</param>
/// <param name="ReadOnly">Whether the lease is read-only.</param>
public sealed record WorktreeLease(string Id, string Path, bool ReadOnly);

/// <summary>A request for a worktree.</summary>
/// <param name="SubagentId">The subagent the worktree is for.</param>
/// <param name="ParentId">The parent session.</param>
/// <param name="ReadOnly">Whether a read-only view is wanted.</param>
/// <param name="DeclaredPaths">Paths a writer declared.</param>
public sealed record WorktreeAllocation(string SubagentId, string ParentId, bool ReadOnly, IReadOnlyList<string> DeclaredPaths);

/// <summary>Port: allocates and releases worktrees for subagents (doc 17).</summary>
public interface ISubagentWorktreeAllocator
{
    /// <summary>Allocates a worktree. A writer's worktree is never shared with another writer.</summary>
    Task<WorktreeLease> AllocateAsync(WorktreeAllocation request, CancellationToken cancellationToken);

    /// <summary>Releases a lease. Called once per lease on success, failure and cancel.</summary>
    Task ReleaseAsync(WorktreeLease lease, CancellationToken cancellationToken);
}
