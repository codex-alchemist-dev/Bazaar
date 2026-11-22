using System.Collections.Concurrent;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace;

namespace Bazaar.Server.Host.Coding;

/// <summary>A coding task to open a worktree for.</summary>
/// <param name="SessionId">The coding session. It is the worktree key the tool bridge uses, so one session holds one worktree.</param>
/// <param name="AgentId">The agent that will write in the worktree.</param>
/// <param name="Repo">The repository and its default branch.</param>
/// <param name="TaskSlug">The task slug; the branch becomes <c>agent/&lt;TaskSlug&gt;</c>.</param>
/// <param name="GcId">The group chat the work belongs to (commit trailer).</param>
/// <param name="TaskId">The task id (commit trailer).</param>
public sealed record CodingTaskStart(Guid SessionId, string AgentId, RepoRef Repo, string TaskSlug, string GcId, string TaskId);

/// <summary>
/// The start and end of a coding task's worktree (doc 17, "Git workflow": one worktree per writer). <see cref="StartAsync"/>
/// opens <c>agent/&lt;task&gt;</c> through <see cref="CodingGitService"/>, so the work-share placement rule, the path and branch
/// locks and the branch name rule all apply, then registers the worktree under the session id for the tool bridge.
/// <see cref="EndAsync"/> releases it. The same record answers <see cref="ISubagentParentWorkSource"/>, so a subagent spawned
/// from the session gets its own worktree on a branch derived from the parent's task slug.
/// This is the contract-first piece: nothing calls <see cref="StartAsync"/> yet. The task-start flow of the Agents turn path
/// does, once it names a repository for a task (doc 17, open item).
/// </summary>
public sealed class CodingTaskSessions(CodingGitService git, CodingWorktreeRegistry registry) : ISubagentParentWorkSource
{
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens the task's worktree and registers it under the session id. Throws <see cref="InvalidOperationException"/> when the
    /// session already holds one, and the git service's own error when another writer holds the worktree or branch.
    /// </summary>
    public async Task<CodingWorktree> StartAsync(CodingTaskStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var key = start.SessionId.ToString("D");
        if (registry.Find(key) is not null)
        {
            throw new InvalidOperationException("This coding session already holds a worktree.");
        }

        var worktree = await git.CreateWorktreeAsync(
            new CreateWorktreeRequest(start.AgentId, start.Repo, start.TaskSlug, start.GcId, start.TaskId),
            cancellationToken).ConfigureAwait(false);

        if (!registry.TryRegister(key, worktree))
        {
            // Lost a race for the same session: give the locks back rather than hold a worktree nothing can reach.
            await worktree.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("This coding session already holds a worktree.");
        }

        _held[key] = new Held(start.TaskSlug, worktree);
        return worktree;
    }

    /// <summary>Releases the session's worktree and forgets it. Returns false when the session held none. The directory and branch stay.</summary>
    public async Task<bool> EndAsync(Guid sessionId)
    {
        var key = sessionId.ToString("D");
        if (!_held.TryRemove(key, out var held))
        {
            return false;
        }

        registry.Remove(key);
        await held.Worktree.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public ValueTask<SubagentParentWork?> FindAsync(string parentId, CancellationToken cancellationToken)
    {
        if (parentId is null || !_held.TryGetValue(parentId, out var held) || !held.Worktree.IsActive)
        {
            return ValueTask.FromResult<SubagentParentWork?>(null);
        }

        var wt = held.Worktree;
        return ValueTask.FromResult<SubagentParentWork?>(new SubagentParentWork(wt.AgentId, wt.Repo, wt.GcId, wt.TaskId, held.TaskSlug, wt.Path));
    }

    private sealed record Held(string TaskSlug, CodingWorktree Worktree);
}
