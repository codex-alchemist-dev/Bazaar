using System.Collections.Concurrent;
using Bazaar.Server.Vault.Git;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Bazaar.Server.GitHub.Workspace;

/// <summary>
/// Host-side git operations for agent work (doc 21, git operations): task worktrees, commits with Bazaar trailers, and
/// pushes of agent branches. Every operation takes a per-repository lock, so two tasks on one repository never run git
/// at the same time. Lower-level writer locks stay in <see cref="IGitProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credentials.</b> A push requests the credential once, and only after the origin host is confirmed as an allowed
/// HTTPS GitHub host. The token is held in memory for that push, is returned to libgit2 once, and is never written to
/// disk, a log, a commit or an exception.
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
    private readonly HashSet<string> _gitHubHosts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _repoPaths = new(StringComparer.Ordinal);

    public GitWorkspace(IGitProvider git, GitWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);

        _git = git;
        _rootPath = Path.GetFullPath(options.RootPath);
        _gitHubHosts = new HashSet<string>(options.GitHubHosts, StringComparer.OrdinalIgnoreCase);
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
        var fullRepoPath = Path.GetFullPath(repoPath);

        using var _ = await LockRepoAsync(repoId, ct, fullRepoPath, KnownRepoPath(repoId)).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        var fullPath = await _git.AddWorktreeAsync(repoPath, branch, worktreePath, ct).ConfigureAwait(false);
        _repoPaths[repoId] = fullRepoPath;
        return new TaskWorktree(repoId, taskId, branch, fullPath);
    }

    /// <summary>Commits every change in the task worktree and returns the new commit SHA.</summary>
    public async Task<string> CommitAsync(TaskWorktree worktree, CommitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Author);

        var message = BuildCommitMessage(request);
        ValidateWorktree(worktree);

        using var _ = await LockRepoAsync(worktree.RepoId, ct, KnownRepoPath(worktree.RepoId)).ConfigureAwait(false);
        return await _git.CommitAsync(worktree.Path, message, request.Author, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pushes the task branch to <c>origin</c> host-side, using a credential requested for this push only. Refuses the
    /// default branch, any branch outside <c>agent/</c>, and any origin that is not an allowed HTTPS GitHub host. Never
    /// force-pushes.
    /// </summary>
    public async Task PushAsync(TaskWorktree worktree, Func<CancellationToken, Task<string>> credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(credential);
        ValidatePushBranch(worktree.Branch);
        ValidateWorktree(worktree);

        using var _ = await LockRepoAsync(worktree.RepoId, ct, KnownRepoPath(worktree.RepoId)).ConfigureAwait(false);

        var origin = await Task.Run(() => ReadOriginUri(worktree.Path), ct).ConfigureAwait(false);
        if (!IsAllowedHost(origin))
        {
            throw new GitWorkspaceException("The origin remote is not an allowed GitHub host, so nothing was pushed.");
        }

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

    /// <summary>
    /// Returns a credentials handler that yields the token once. A second request during the same push throws, so the
    /// engine cannot retry with the same secret. A remote that does not accept username and password is refused.
    /// </summary>
    internal static CredentialsHandler CreateOneShotCredentials(string token)
    {
        var issued = 0;
        return (_, _, types) =>
        {
            if (Interlocked.Exchange(ref issued, 1) != 0)
            {
                throw new GitWorkspaceException("The push credential was already issued for this push.");
            }

            if ((types & SupportedCredentialTypes.UsernamePassword) == 0)
            {
                throw new GitWorkspaceException("The remote does not accept username and password credentials.");
            }

            return new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = token,
            };
        };
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

    /// <summary>Checks that the worktree is one this workspace created: path, branch and ids must agree.</summary>
    private void ValidateWorktree(TaskWorktree worktree)
    {
        if (!IsValidSegment(worktree.RepoId)
            || !IsValidSegment(worktree.TaskId)
            || worktree.Branch != _agentBranchPrefix + worktree.TaskId
            || string.IsNullOrWhiteSpace(worktree.Path)
            || !PathsEqual(worktree.Path, Path.Combine(_rootPath, worktree.RepoId, worktree.TaskId)))
        {
            throw new GitWorkspaceException("The worktree does not belong to this workspace.");
        }
    }

    private static bool PathsEqual(string candidate, string expected)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(expected), comparison);
    }

    private static Uri? ReadOriginUri(string worktreePath)
    {
        using var repo = new Repository(worktreePath);
        var url = repo.Network.Remotes[_pushRemote]?.Url;
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private bool IsAllowedHost(Uri? origin) =>
        origin is not null && origin.Scheme == Uri.UriSchemeHttps && _gitHubHosts.Contains(origin.Host);

    private static void PushBranch(string worktreePath, string branch, string token)
    {
        using var repo = new Repository(worktreePath);
        var remote = repo.Network.Remotes[_pushRemote]
            ?? throw new GitWorkspaceException($"The repository has no '{_pushRemote}' remote.");

        var options = new PushOptions
        {
            CredentialsProvider = CreateOneShotCredentials(token),
        };

        var refspec = $"refs/heads/{branch}:refs/heads/{branch}";
        repo.Network.Push(remote, refspec, options);
    }

    private static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "[redacted]", StringComparison.Ordinal);

    private static void ValidateSegment(string value, string paramName)
    {
        if (!IsValidSegment(value))
        {
            throw new ArgumentException("The value must be a short ASCII identifier made of letters, digits, '.', '_' or '-'.", paramName);
        }
    }

    private static bool IsValidSegment(string? value) =>
        value is { Length: > 0 and <= _maxSegmentLength }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(IsSegmentChar)
        && !value.Contains("..", StringComparison.Ordinal);

    private static bool IsSegmentChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private string? KnownRepoPath(string repoId) => _repoPaths.TryGetValue(repoId, out var path) ? path : null;

    /// <summary>
    /// Takes the lock for the repository id and for each known canonical repository path, so two ids that point at one
    /// path serialize. Locks are taken in sorted key order, which keeps concurrent callers deadlock-free.
    /// </summary>
    private async Task<RepoLease> LockRepoAsync(string repoId, CancellationToken ct, params string?[] repoPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        var keys = new SortedSet<string>(StringComparer.Ordinal) { "id:" + repoId };
        foreach (var path in repoPaths)
        {
            if (path is not null)
            {
                keys.Add("path:" + path);
            }
        }

        var gates = new List<SemaphoreSlim>(keys.Count);
        try
        {
            foreach (var key in keys)
            {
                var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                gates.Add(gate);
            }
        }
        catch
        {
            foreach (var gate in gates)
            {
                gate.Release();
            }

            throw;
        }

        return new RepoLease(gates);
    }

    private sealed class RepoLease(IReadOnlyList<SemaphoreSlim> gates) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                foreach (var gate in gates)
                {
                    gate.Release();
                }
            }
        }
    }
}
