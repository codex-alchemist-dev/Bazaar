using System.Collections.Concurrent;
using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.GitHub.Workspace;

/// <summary>
/// Host-side git operations for agent work (doc 21, git operations): task worktrees, commits with Bazaar trailers, and
/// pushes of agent branches. Every operation takes a per-repository lock, so two tasks on one repository never run git
/// at the same time. Lower-level writer locks stay in <see cref="IGitProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credentials.</b> A push receives a credential source for one push only. The token is held in memory for that call,
/// is passed to libgit2 as a credential callback, and is never written to disk, a log, a commit or an exception.
/// </para>
/// <para>
/// <b>Refusals.</b> The default branch (<c>main</c>) and any branch outside <c>agent/</c> are refused before the lock is
/// taken and before the credential is requested. Commit messages that are empty or contain <c>Signed-off-by</c> are
/// refused before anything is written. Agents never add sign-offs (doc 21).
/// </para>
/// </remarks>
public sealed class GitWorkspace
{
    private const string _agentBranchPrefix = "agent/";
    private const string _defaultBranch = "main";
    private const string _pushRemote = "origin";
    private const string _signOffTrailer = "Signed-off-by";
    private const int _maxSegmentLength = 128;

    private readonly IGitProvider _git;
    private readonly string _rootPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.Ordinal);

    public GitWorkspace(IGitProvider git, GitWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);

        _git = git;
        _rootPath = Path.GetFullPath(options.RootPath);
    }

    /// <summary>
    /// Creates the worktree for a task at <c>&lt;RootPath&gt;/&lt;repoId&gt;/&lt;taskId&gt;</c> on branch <c>agent/&lt;taskId&gt;</c>.
    /// The branch is created from HEAD of the repository when it does not exist.
    /// </summary>
    public async Task<TaskWorktree> CreateTaskWorktreeAsync(string repoId, string repoPath, string taskId, CancellationToken ct)
    {
        ValidateSegment(repoId, nameof(repoId));
        ValidateSegment(taskId, nameof(taskId));
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        var branch = _agentBranchPrefix + taskId;
        var worktreePath = Path.Combine(_rootPath, repoId, taskId);

        using var _ = await LockRepoAsync(repoId, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        var fullPath = await _git.AddWorktreeAsync(repoPath, branch, worktreePath, ct).ConfigureAwait(false);
        return new TaskWorktree(repoId, taskId, branch, fullPath);
    }

    /// <summary>Commits every change in the task worktree and returns the new commit SHA.</summary>
    public async Task<string> CommitAsync(TaskWorktree worktree, CommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Author);

        var message = BuildCommitMessage(request);

        using var _ = await LockRepoAsync(worktree.RepoId, ct).ConfigureAwait(false);
        return await _git.CommitAsync(worktree.Path, message, request.Author, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes the task branch to <c>origin</c> host-side, using a credential requested for this push only. Refuses the
    /// default branch and any branch outside <c>agent/</c>. Never force-pushes.
    /// </summary>
    public async Task PushAsync(TaskWorktree worktree, Func<CancellationToken, Task<string>> credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(credential);
        ValidatePushBranch(worktree.Branch);

        using var _ = await LockRepoAsync(worktree.RepoId, ct).ConfigureAwait(false);

        string? token = null;
        try
        {
            token = await credential(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                throw new GitWorkspaceException("The push credential is empty.");
            }

            var pushedToken = token;
            await Task.Run(() => PushBranch(worktree.Path, worktree.Branch, pushedToken), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The engine exception is not chained: its message is the only text kept, and the token is removed from it.
            throw new GitWorkspaceException(
                $"Push of '{worktree.Branch}' failed ({ex.GetType().Name}): {Redact(ex.Message, token)}");
        }
    }

    internal static void ValidatePushBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new ArgumentException("A branch name is required.", nameof(branch));
        }

        if (branch == _defaultBranch)
        {
            throw new ArgumentException($"Pushing the default branch '{_defaultBranch}' is refused.", nameof(branch));
        }

        if (!branch.StartsWith(_agentBranchPrefix, StringComparison.Ordinal) || branch.Length == _agentBranchPrefix.Length)
        {
            throw new ArgumentException($"Only branches under '{_agentBranchPrefix}' can be pushed.", nameof(branch));
        }

        if (!Reference.IsValidName("refs/heads/" + branch))
        {
            throw new ArgumentException("The branch name is not a valid git reference.", nameof(branch));
        }
    }

    internal static string BuildCommitMessage(CommitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            throw new ArgumentException("The commit message is empty.", nameof(request));
        }

        var trailers = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.CoAuthoredBy))
        {
            trailers.Add(Trailer("Co-Authored-By", request.CoAuthoredBy));
        }

        trailers.Add(Trailer("Bazaar-Agent", request.AgentId));
        trailers.Add(Trailer("Bazaar-GC", request.GcId));
        trailers.Add(Trailer("Bazaar-Task", request.TaskId));

        var message = request.Message.TrimEnd() + "\n\n" + string.Join('\n', trailers);
        if (message.Contains(_signOffTrailer, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Agent commits must not contain a Signed-off-by trailer.", nameof(request));
        }

        return message;
    }

    private static string Trailer(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException($"The {name} trailer must be a single non-empty line.", nameof(value));
        }

        return $"{name}: {value.Trim()}";
    }

    private static void PushBranch(string worktreePath, string branch, string token)
    {
        using var repo = new Repository(worktreePath);
        var remote = repo.Network.Remotes[_pushRemote]
            ?? throw new GitWorkspaceException($"The repository has no '{_pushRemote}' remote.");

        var options = new PushOptions
        {
            CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = token,
            },
        };

        var refspec = $"refs/heads/{branch}:refs/heads/{branch}";
        repo.Network.Push(remote, refspec, options);
    }

    private static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[redacted]", StringComparison.Ordinal);

    private static void ValidateSegment(string value, string paramName)
    {
        if (value is not { Length: > 0 and <= _maxSegmentLength }
            || !char.IsAsciiLetterOrDigit(value[0])
            || !value.All(IsSegmentChar)
            || value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The value must be a short ASCII identifier made of letters, digits, '.', '_' or '-'.", paramName);
        }
    }

    private static bool IsSegmentChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private async Task<RepoLease> LockRepoAsync(string repoId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        var gate = _repoLocks.GetOrAdd(repoId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new RepoLease(gate);
    }

    private sealed class RepoLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
