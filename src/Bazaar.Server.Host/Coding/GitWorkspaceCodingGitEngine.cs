using System.Collections.Concurrent;
using System.Text;
using Bazaar.GitHub.Workspace;
using Bazaar.Server.CodingGit;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Host.Coding;

/// <summary>Where a repository lives and which GitHub App installation serves it. Supplied by the host from its repository records.</summary>
/// <param name="RepoPath">The local clone that task worktrees are added from.</param>
/// <param name="InstallationId">The GitHub App installation that covers the repository.</param>
/// <param name="Owner">The GitHub owner.</param>
/// <param name="Repo">The GitHub repository name.</param>
/// <param name="RepositoryId">The numeric GitHub repository id, for the narrowed push token.</param>
public sealed record CodingRepoBinding(string RepoPath, long InstallationId, string Owner, string Repo, long RepositoryId);

/// <summary>Host lookup from the harness's short repository id to its binding. Returns null for an unknown id, which refuses the call.</summary>
public interface ICodingRepoDirectory
{
    /// <summary>Finds the binding for a repository id.</summary>
    ValueTask<CodingRepoBinding?> FindAsync(string repoId, CancellationToken cancellationToken);
}

/// <summary>
/// Binds the harness's <see cref="ICodingGitEngine"/> to the host-side <see cref="GitWorkspace"/> (doc 21, git operations).
/// A worktree path must be <c>&lt;workspaceRoot&gt;/&lt;repoId&gt;/&lt;taskId&gt;</c> on branch <c>agent/&lt;taskId&gt;</c>, the
/// layout <see cref="GitWorkspace"/> enforces; any other path is refused. With an <see cref="IWorkShareLayout"/>, the root is the
/// agent's own work share instead (the host <see cref="GitWorkspace"/> must be given a resolver over the same layout). The agent is
/// known from the add request and the commit trailers; a diff finds it from the add that created the worktree, and otherwise uses the
/// global root.
/// </summary>
public sealed class GitWorkspaceCodingGitEngine : ICodingGitEngine
{
    private readonly GitWorkspace _workspace;
    private readonly string _root;
    private readonly ICodingRepoDirectory _repos;
    private readonly IWorkShareLayout? _layout;
    private readonly ConcurrentDictionary<string, string> _agentByPath = new(StringComparer.Ordinal);

    /// <summary>Creates the engine.</summary>
    /// <param name="workspace">The host workspace.</param>
    /// <param name="workspaceRoot">The same root the workspace was configured with (<see cref="GitWorkspaceOptions.RootPath"/>).</param>
    /// <param name="repos">Repository lookup.</param>
    /// <param name="layout">Per-agent work-share roots; null keeps the single global root.</param>
    public GitWorkspaceCodingGitEngine(GitWorkspace workspace, string workspaceRoot, ICodingRepoDirectory repos, IWorkShareLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(repos);
        _workspace = workspace;
        _root = Path.GetFullPath(workspaceRoot);
        _repos = repos;
        _layout = layout;
    }

    /// <inheritdoc />
    public async Task AddWorktreeAsync(AddWorktreeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var worktree = WorktreeFor(request.Repo.Id, request.Branch, request.Path, request.AgentId);
        var binding = await _repos.FindAsync(request.Repo.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new CodingGitException(CodingGitError.InvalidRequest, "The repository is not known to the host.");
        await _workspace.CreateTaskWorktreeAsync(worktree.AgentId, worktree.RepoId, binding.RepoPath, worktree.TaskId, cancellationToken).ConfigureAwait(false);
        if (worktree.AgentId is not null)
        {
            _agentByPath[worktree.Path] = worktree.AgentId;
        }
    }

    /// <inheritdoc />
    public async Task<CommitId> CommitAsync(EngineCommitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var worktree = WorktreeFromPath(request.WorktreePath, request.Trailers.AgentId);
        var commit = new CommitRequest(
            request.Message,
            new GitIdentity(request.Author.Name, request.Author.Email),
            request.Trailers.AgentId,
            request.Trailers.GcId,
            request.Trailers.TaskId,
            request.Trailers.CoAuthoredBy);
        return new CommitId(await _workspace.CommitAsync(worktree, commit, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<GitDiff> DiffAgainstBaseAsync(string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        var known = string.IsNullOrWhiteSpace(worktreePath) ? null : _agentByPath.GetValueOrDefault(Path.GetFullPath(worktreePath));
        var worktree = WorktreeFromPath(worktreePath, known);
        var files = await _workspace.DiffAsync(worktree, baseBranch, cancellationToken).ConfigureAwait(false);
        var patch = new StringBuilder();
        foreach (var file in files)
        {
            patch.Append(file.Patch);
            if (file.Patch.Length > 0 && !file.Patch.EndsWith('\n'))
            {
                patch.Append('\n');
            }
        }

        return new GitDiff(baseBranch, files.Select(f => f.Path).ToArray(), patch.ToString());
    }

    private TaskWorktree WorktreeFor(string repoId, BranchName branch, string path, string? agentId)
    {
        var worktree = WorktreeFromPath(path, agentId);
        if (!string.Equals(worktree.RepoId, repoId, StringComparison.Ordinal)
            || !string.Equals(worktree.Branch, branch.Value, StringComparison.Ordinal))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "The worktree path does not match the repository and branch.");
        }

        return worktree;
    }

    private TaskWorktree WorktreeFromPath(string path, string? agentId)
    {
        var root = agentId is not null && _layout is not null ? Path.GetFullPath(_layout.GetWorktreeRoot(agentId)) : _root;
        var relative = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetRelativePath(root, Path.GetFullPath(path));
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p == ".."))
        {
            throw new CodingGitException(CodingGitError.WorktreeOutsideWorkShare, "The worktree is not at <root>/<repo>/<task> of the host workspace.");
        }

        return new TaskWorktree(parts[0], parts[1], BranchName.Prefix + parts[1], Path.Combine(root, parts[0], parts[1]), _layout is null ? null : agentId);
    }
}
