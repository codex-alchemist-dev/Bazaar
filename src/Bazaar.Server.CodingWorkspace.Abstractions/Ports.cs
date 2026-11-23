using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>One agent's work share: where its worktrees go and where its Passport folder is (doc 16, inter-VM shares).</summary>
/// <param name="WorktreeRoot">Absolute root of the agent's separate work share.</param>
/// <param name="PassportFolder">Absolute Passport folder; never equal to, under or around the work share.</param>
public sealed record AgentWorkShare(string WorktreeRoot, string PassportFolder);

/// <summary>
/// Port: where one agent's shares are. The shipped source is the operator-configured root; the VM mounts module (doc 16, WP8)
/// replaces it when it owns the share layout.
/// </summary>
public interface IAgentWorkShareSource
{
    /// <summary>The shares for an agent, or null when the agent has none. A null result refuses the call (fail closed).</summary>
    AgentWorkShare? Find(string agentId);
}

/// <summary>
/// One repository the harness may work in. Mirrors the Git module's repository directory entry (doc 52) with the numeric ids
/// the GitHub adapter needs for the narrowed push token.
/// </summary>
/// <param name="Repo">Short id and default branch.</param>
/// <param name="ClonePath">Absolute host-side clone that task worktrees are added from (never a VM path).</param>
/// <param name="InstallationId">The GitHub App installation that covers the repository.</param>
/// <param name="Owner">The GitHub owner.</param>
/// <param name="Name">The GitHub repository name.</param>
/// <param name="RepositoryId">The numeric GitHub repository id.</param>
public sealed record CodingRepoRecord(RepoRef Repo, string ClonePath, long InstallationId, string Owner, string Name, long RepositoryId);

/// <summary>Port: finds a repository by the harness's short repository id.</summary>
public interface ICodingRepoSource
{
    /// <summary>The repository, or null when it is unknown or its clone is not usable.</summary>
    ValueTask<CodingRepoRecord?> FindAsync(string repoId, CancellationToken cancellationToken);
}

/// <summary>What the allocator needs to know about a parent session to give its child a worktree.</summary>
/// <param name="AgentId">The agent that owns the parent session.</param>
/// <param name="Repo">The repository the parent works in.</param>
/// <param name="GcId">The group chat the work belongs to (commit trailer).</param>
/// <param name="TaskId">The parent's task id (commit trailer).</param>
/// <param name="TaskSlug">The parent's task slug; the child's branch is derived from it.</param>
/// <param name="WorktreePath">The parent's own worktree path, the view a read-only child gets.</param>
public sealed record SubagentParentWork(string AgentId, RepoRef Repo, string GcId, string TaskId, string TaskSlug, string WorktreePath);

/// <summary>Port: resolves a parent session id to its work. The host binds it from the running-turn registry.</summary>
public interface ISubagentParentWorkSource
{
    /// <summary>The parent's work, or null when the session is unknown or has no worktree.</summary>
    ValueTask<SubagentParentWork?> FindAsync(string parentId, CancellationToken cancellationToken);
}
