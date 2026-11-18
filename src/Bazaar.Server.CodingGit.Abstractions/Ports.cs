namespace Bazaar.Server.CodingGit;

/// <summary>A repository the agent works in. The default branch is the push guard's reference.</summary>
/// <param name="Id">Short repository id, used as a directory name under the agent's worktree root. Charset [A-Za-z0-9._-].</param>
/// <param name="DefaultBranch">The repository's default branch name, for example <c>main</c>.</param>
public sealed record RepoRef(string Id, string DefaultBranch);

/// <summary>The author written on a commit: the agent's GitHub App identity (doc 21). Never carries a token.</summary>
/// <param name="Name">Author name.</param>
/// <param name="Email">Author email.</param>
public sealed record GitAuthor(string Name, string Email);

/// <summary>A commit id.</summary>
/// <param name="Sha">The commit hash.</param>
public sealed record CommitId(string Sha);

/// <summary>Request to add a worktree on a new or existing agent branch.</summary>
/// <param name="Repo">The repository.</param>
/// <param name="Branch">The branch to check out in the worktree.</param>
/// <param name="Path">Canonical absolute worktree path, already checked by the service.</param>
public sealed record AddWorktreeRequest(RepoRef Repo, BranchName Branch, string Path);

/// <summary>
/// Trailers the engine writes on a commit. Shaped to match the host workspace's commit request (<c>Bazaar-Agent</c>,
/// <c>Bazaar-GC</c>, <c>Bazaar-Task</c> and <c>Co-Authored-By</c>), so an adapter maps one to one. There is no
/// sign-off field: agents never add <c>Signed-off-by</c>.
/// </summary>
/// <param name="AgentId">The committing agent.</param>
/// <param name="GcId">The group chat the work belongs to.</param>
/// <param name="TaskId">The task id.</param>
/// <param name="CoAuthoredBy">The model credit as <c>Name &lt;email&gt;</c>.</param>
public sealed record CommitTrailers(string AgentId, string GcId, string TaskId, string CoAuthoredBy);

/// <summary>Request to commit the worktree's pending changes.</summary>
/// <param name="WorktreePath">Canonical absolute worktree path.</param>
/// <param name="Message">The validated commit subject and body, without trailers.</param>
/// <param name="Trailers">The validated trailers the engine appends.</param>
/// <param name="Author">The commit author and committer.</param>
public sealed record EngineCommitRequest(string WorktreePath, string Message, CommitTrailers Trailers, GitAuthor Author);

/// <summary>The worktree's changes against its base branch.</summary>
/// <param name="BaseBranch">The branch compared against.</param>
/// <param name="ChangedPaths">Repository-relative paths that differ.</param>
/// <param name="Patch">The unified diff text.</param>
public sealed record GitDiff(string BaseBranch, IReadOnlyList<string> ChangedPaths, string Patch);

/// <summary>
/// Git operations on one worktree, run wherever the engine runs (doc 17, Git workflow). Push is not here: it is
/// host-side through <see cref="IHostPushPort"/>.
/// </summary>
public interface ICodingGitEngine
{
    /// <summary>
    /// Adds a worktree at the given path on the given branch, creating the branch from the repository's default
    /// branch when it does not exist. Calling it again for the same path and branch returns without change.
    /// </summary>
    Task AddWorktreeAsync(AddWorktreeRequest request, CancellationToken cancellationToken);

    /// <summary>Commits all pending changes in the worktree. Fails with <see cref="CodingGitError.NothingToCommit"/> when there are none.</summary>
    Task<CommitId> CommitAsync(EngineCommitRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the worktree's changes against <paramref name="baseBranch"/>.</summary>
    Task<GitDiff> DiffAgainstBaseAsync(string worktreePath, string baseBranch, CancellationToken cancellationToken);
}

/// <summary>Request to push an <c>agent/*</c> branch and open a draft pull request. There is no draft flag: it is always a draft.</summary>
/// <param name="AgentId">The pushing agent.</param>
/// <param name="Repo">The repository.</param>
/// <param name="Branch">The branch to push.</param>
/// <param name="Title">Pull request title.</param>
/// <param name="Body">Pull request body.</param>
public sealed record HostPushRequest(string AgentId, RepoRef Repo, BranchName Branch, string Title, string Body);

/// <summary>A pull request opened by the host.</summary>
/// <param name="Repo">The repository id.</param>
/// <param name="Number">The pull request number.</param>
/// <param name="Url">The pull request URL.</param>
/// <param name="IsDraft">Whether the pull request is a draft. The service accepts only true.</param>
public sealed record PullRequestRef(string Repo, int Number, string Url, bool IsDraft);

/// <summary>
/// Host-side push of an <c>agent/*</c> branch and draft pull request through doc 21. The host holds the credential
/// (GitHub App installation token); no token or secret crosses this port in either direction.
/// </summary>
public interface IHostPushPort
{
    /// <summary>Pushes the branch and opens a draft pull request.</summary>
    Task<PullRequestRef> PushDraftPrAsync(HostPushRequest request, CancellationToken cancellationToken);
}

/// <summary>Where an agent's worktrees and Passport folder live (doc 16 inter-VM shares).</summary>
public interface IWorkShareLayout
{
    /// <summary>The root of the agent's separate work share, where its worktrees go.</summary>
    string GetWorktreeRoot(string agentId);

    /// <summary>The agent's Passport folder. No worktree may be under, equal to, or contain it.</summary>
    string GetPassportFolder(string agentId);
}

/// <summary>The agent's GitHub App identity for commit authorship (doc 21).</summary>
public interface IAgentGitIdentity
{
    /// <summary>Returns the author for the agent.</summary>
    ValueTask<GitAuthor> GetAuthorAsync(string agentId, CancellationToken cancellationToken);
}

/// <summary>What the push gate decided.</summary>
public enum PushVerdict
{
    /// <summary>Push may proceed.</summary>
    Allow,

    /// <summary>A recorded approval is needed first (hard-tainted session, doc 17 Permissions).</summary>
    NeedsApproval,

    /// <summary>Push is not allowed.</summary>
    Deny,
}

/// <summary>A gate decision with a reason for the audit trail.</summary>
/// <param name="Verdict">The decision.</param>
/// <param name="Reason">Short reason. Never contains secrets.</param>
public sealed record PushDecision(PushVerdict Verdict, string Reason)
{
    /// <summary>Allow.</summary>
    public static PushDecision Allow(string reason = "allowed") => new(PushVerdict.Allow, reason);

    /// <summary>Needs approval.</summary>
    public static PushDecision NeedsApproval(string reason) => new(PushVerdict.NeedsApproval, reason);

    /// <summary>Deny.</summary>
    public static PushDecision Deny(string reason) => new(PushVerdict.Deny, reason);
}

/// <summary>What the gate is asked about.</summary>
/// <param name="AgentId">The pushing agent.</param>
/// <param name="Repo">The repository.</param>
/// <param name="Branch">The branch.</param>
/// <param name="GcId">The group chat the work belongs to.</param>
/// <param name="TaskId">The task id.</param>
public sealed record PushGateRequest(string AgentId, RepoRef Repo, BranchName Branch, string GcId, string TaskId);

/// <summary>
/// Minimal port to the permission gate (doc 17 Permissions: pushes to <c>agent/*</c> need no approval while
/// untainted, per-push approval while hard-tainted). The real gate is another module.
/// </summary>
public interface IPushGate
{
    /// <summary>Decides whether this push is allowed.</summary>
    ValueTask<PushDecision> EvaluateAsync(PushGateRequest request, CancellationToken cancellationToken);
}

/// <summary>A held lock. Disposing it releases the lock; disposing twice is safe.</summary>
public interface IWorktreeLease : IAsyncDisposable
{
    /// <summary>The locked key.</summary>
    string Key { get; }

    /// <summary>True while this lease still owns the key and has not expired.</summary>
    bool IsHeld { get; }

    /// <summary>Extends the lease by its original duration. False when it was lost or expired.</summary>
    bool TryRenew();
}

/// <summary>Host-side writer locks. Acquiring never waits: a held key fails fast.</summary>
public interface IWorktreeLocks
{
    /// <summary>
    /// Takes the lock for <paramref name="key"/>, or returns null when another live lease holds it. An expired lease
    /// counts as released, so a crashed holder cannot block the key forever.
    /// </summary>
    ValueTask<IWorktreeLease?> TryAcquireAsync(string key, TimeSpan leaseDuration, CancellationToken cancellationToken);
}
