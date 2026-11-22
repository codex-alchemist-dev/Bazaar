using Bazaar.Server.Vault.Git;

namespace Bazaar.GitHub.Workspace;

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
/// <param name="AgentId">The agent whose work share holds the worktree; null for the global root.</param>
public sealed record TaskWorktree(string RepoId, string TaskId, string Branch, string Path, string? AgentId = null);

/// <summary>
/// Resolves the work-share root of one agent (doc 16, inter-VM shares). Returning null means "use the global
/// <see cref="GitWorkspaceOptions.RootPath"/>", which is also what happens when no resolver is supplied.
/// </summary>
public interface IWorkShareRootResolver
{
    /// <summary>Returns the agent's worktree root, or null for the global root.</summary>
    string? GetRoot(string agentId);
}

/// <summary>One file of a worktree's diff against a base ref.</summary>
/// <param name="Path">The path in the worktree (the old path for deletions), '/' separated.</param>
/// <param name="OldPath">The previous path for renames and copies; otherwise null.</param>
/// <param name="Kind">What happened to the file.</param>
/// <param name="Patch">The unified diff text for the file; empty for binary files.</param>
public sealed record FileDiff(string Path, string? OldPath, DiffChangeKind Kind, string Patch);

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
