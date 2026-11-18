using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.GitHub.Workspace;

/// <summary>Settings for <see cref="GitWorkspace"/>.</summary>
public sealed class GitWorkspaceOptions
{
    /// <summary>
    /// Directory that holds task worktrees. A worktree lives at <c>&lt;RootPath&gt;/&lt;repoId&gt;/&lt;taskId&gt;</c>.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>Hosts whose HTTPS origin may receive the push credential. Only <c>github.com</c> by default.</summary>
    public IReadOnlyList<string> GitHubHosts { get; init; } = ["github.com"];
}

/// <summary>A worktree for one task, on its own <c>agent/&lt;task&gt;</c> branch.</summary>
/// <param name="RepoId">The repository the worktree belongs to. Also the key of the per-repo lock.</param>
/// <param name="TaskId">The task that owns the branch.</param>
/// <param name="Branch">The branch checked out in the worktree, always <c>agent/&lt;task&gt;</c>.</param>
/// <param name="Path">The full path of the worktree directory.</param>
public sealed record TaskWorktree(string RepoId, string TaskId, string Branch, string Path);

/// <summary>
/// A commit made in a task worktree. The trailers <c>Bazaar-Agent</c>, <c>Bazaar-GC</c> and <c>Bazaar-Task</c> are
/// always written; <c>Co-Authored-By</c> is written when <see cref="CoAuthoredBy"/> is set (doc 21, git operations).
/// </summary>
/// <param name="Message">The commit subject and body, without trailers. Must not be empty.</param>
/// <param name="Author">The author identity, normally the agent's GitHub App bot identity.</param>
/// <param name="AgentId">The agent that made the commit.</param>
/// <param name="GcId">The GC the work belongs to.</param>
/// <param name="TaskId">The task the work belongs to.</param>
/// <param name="CoAuthoredBy">The model credit written as <c>Co-Authored-By</c>, such as <c>Name &lt;email&gt;</c>.</param>
public sealed record CommitRequest(
    string Message,
    GitIdentity Author,
    string AgentId,
    string GcId,
    string TaskId,
    string? CoAuthoredBy = null);

/// <summary>
/// A git operation was refused or failed inside <see cref="GitWorkspace"/>. The message never contains a credential, and
/// the exception has no inner exception, because an engine exception may echo the credential.
/// </summary>
public sealed class GitWorkspaceException(string message) : Exception(message);
