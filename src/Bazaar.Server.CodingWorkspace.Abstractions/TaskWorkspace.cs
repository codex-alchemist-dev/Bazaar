namespace Bazaar.Server.CodingWorkspace;

/// <summary>Why <see cref="ICodingTaskWorkspace"/> refused a call.</summary>
public enum CodingTaskWorkspaceError
{
    /// <summary>The agent id is blank or not a safe id, or the agent has no work share.</summary>
    InvalidAgent,

    /// <summary>The task id (or the group chat id) is not usable as a branch slug and a path segment.</summary>
    InvalidTask,

    /// <summary>No repo id was given and none is configured as the default.</summary>
    NoRepo,

    /// <summary>The repo is unknown, or its clone is not usable.</summary>
    UnknownRepo,

    /// <summary>A worktree for this task already exists on disk or is already open.</summary>
    AlreadyExists,

    /// <summary>The worktree could not be created (git, lock or work-share refusal). The inner exception says why.</summary>
    OpenFailed,

    /// <summary>The worktree could not be removed.</summary>
    DiscardFailed,
}

/// <summary>A refused <see cref="ICodingTaskWorkspace"/> call. Nothing is held and nothing is registered when it is thrown.</summary>
public sealed class CodingTaskWorkspaceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CodingTaskWorkspaceException(CodingTaskWorkspaceError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    /// <summary>Why the call was refused.</summary>
    public CodingTaskWorkspaceError Error { get; }
}

/// <summary>An open task worktree. Carries no credentials.</summary>
/// <param name="WorktreeKey">The server-issued, unguessable key the worktree is registered under.</param>
/// <param name="AgentId">The agent that writes in it.</param>
/// <param name="RepoId">The repository id.</param>
/// <param name="TaskId">The task id (also the branch slug and directory name).</param>
/// <param name="Branch">The branch checked out, <c>agent/&lt;taskId&gt;</c>.</param>
/// <param name="Path">The absolute worktree path under the agent's work share.</param>
public sealed record CodingTaskWorktree(string WorktreeKey, string AgentId, string RepoId, string TaskId, string Branch, string Path);

/// <summary>
/// Opens and closes the per-task worktree an agent writes in (doc 17, "Default repo and per-task worktree flow"). One worktree
/// per task, on branch <c>agent/&lt;taskId&gt;</c>, under the agent's work share, registered for the tool host context.
/// </summary>
public interface ICodingTaskWorkspace
{
    /// <summary>
    /// Opens the worktree for a task. Refuses (see <see cref="CodingTaskWorkspaceError"/>) an unknown repo, an unsafe task id and a
    /// worktree that already exists.
    /// </summary>
    /// <param name="agentId">The agent that will write.</param>
    /// <param name="taskId">The task id; used as the branch slug and directory name, so it must be one safe lowercase slug.</param>
    /// <param name="repoId">The repo id, or null for the operator's default.</param>
    /// <param name="gcId">The group chat for the commit trailer, or null to use the task id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<CodingTaskWorktree> OpenAsync(string agentId, string taskId, string? repoId = null, string? gcId = null, CancellationToken cancellationToken = default);

    /// <summary>Unregisters the worktree and releases its locks. The directory and branch stay. An unknown key is a no-op.</summary>
    Task CloseAsync(string worktreeKey, CancellationToken cancellationToken = default);

    /// <summary>Closes the worktree and deletes its directory. The branch stays. An unknown key is a no-op.</summary>
    Task DiscardAsync(string worktreeKey, CancellationToken cancellationToken = default);
}
