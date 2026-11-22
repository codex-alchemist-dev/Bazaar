using LibGit2Sharp;

namespace Bazaar.Server.Git;

/// <summary>
/// The default <see cref="IGitProvider"/> (doc 52, spike S23). LibGit2Sharp with its bundled native libgit2, so no git
/// executable is needed on the host. Every call takes the per-repo (or per-workspace) lock from <see cref="RepoLocks"/>,
/// opens its own <see cref="Repository"/> and disposes it. Tokens come from <see cref="IGitCredentialSource"/>, live only
/// for the network call and are never written to a message, log or exception.
/// </summary>
public sealed class LibGit2GitProvider : IGitProvider
{
    private const string _repoMarkerFile = "bazaar-repo";
    private const int _maxBranchLength = 200;
    private const int _maxWorktreeScanDepth = 8;
    private static readonly string[] _mergeStateNames = ["MERGE_HEAD", "MERGE_MSG", "MERGE_MODE", "MERGE_RR"];
    private static readonly string[] _protectedBranches = ["main", "master"];

    private readonly GitEngineOptions _options;
    private readonly IGitCredentialSource _credentials;
    private readonly RepoLocks _locks;

    public LibGit2GitProvider(GitEngineOptions options, IGitCredentialSource credentials, RepoLocks locks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(locks);
        _options = options;
        _credentials = credentials;
        _locks = locks;
    }

    public async Task<WorktreeHandle> AddWorktreeAsync(RepoRef repo, BranchName branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(branch);
        ValidateBranch(branch.Value);
        var root = _options.RepoRoot(repo);
        var worktreePath = WorktreePath(repo, branch);

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(root), cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
                {
                    throw new GitEngineException($"Worktree path '{worktreePath}' already exists.");
                }

                using var git = OpenRepository(root);
                if (git.Branches[branch.Value] is not null)
                {
                    throw new GitEngineException($"Branch '{branch.Value}' already exists.");
                }

                if (git.Head.Tip is null)
                {
                    throw new GitEngineException("The repository has no commits to branch from.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
                var created = git.CreateBranch(branch.Value);
                git.Worktrees.Add(created.CanonicalName, "wt-" + branch.Value.Replace('/', '-'), worktreePath, false);

                // The marker lets PushAsync (which only gets a path) lease the right credential. It lives in the
                // worktree's admin directory, so pruning the worktree removes it too.
                using var worktreeRepo = OpenRepository(worktreePath);
                File.WriteAllText(Path.Combine(worktreeRepo.Info.Path, _repoMarkerFile), repo.Provider + "\n" + repo.FullName + "\n");
                return new WorktreeHandle(worktreePath, branch);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommitId> CommitAsync(WorktreeHandle worktree, CommitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        if (request.Paths.Count == 0)
        {
            throw new ArgumentException("A commit must list at least one path.", nameof(request));
        }

        var workdir = Path.GetFullPath(worktree.Path);
        var relative = request.Paths.Select(p => RelativeInside(workdir, p)).ToList();

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(workdir), cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var git = OpenRepository(workdir);
                foreach (var entry in git.RetrieveStatus())
                {
                    if ((entry.State & _stagedFlags) != 0 && !relative.Any(p => Covers(p, entry.FilePath)))
                    {
                        throw new GitEngineException($"'{entry.FilePath}' is already staged but is not in the commit's paths.");
                    }
                }

                foreach (var path in relative)
                {
                    Commands.Stage(git, path);
                }

                var author = MakeSignature(_options.BotAuthor);
                var committer = MakeSignature(_options.HostCommitter);
                // The message goes in unchanged: the caller supplies any trailers, and the engine never adds Signed-off-by.
                return new CommitId(git.Commit(request.Message, author, committer).Sha);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PushAsync(WorkspaceRef workspace, BranchName branch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(branch);
        ValidateBranch(branch.Value);
        var workdir = Path.GetFullPath(workspace.Path);

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(workdir), cancellationToken).ConfigureAwait(false);
        var repoRef = await Task.Run(() => ReadChecks(workdir, branch), cancellationToken).ConfigureAwait(false);

        using var credential = await _credentials.LeaseAsync(repoRef, GitOperation.Push, cancellationToken).ConfigureAwait(false);
        var secret = credential.Reveal();
        await Task.Run(
            () =>
            {
                try
                {
                    using var git = OpenRepository(workdir);
                    var remote = git.Network.Remotes[_options.RemoteName]
                        ?? throw new GitEngineException($"Remote '{_options.RemoteName}' is not configured.");
                    string? rejected = null;
                    var attempts = 0;
                    var pushOptions = new PushOptions
                    {
                        CredentialsProvider = (_, _, _) => LeaseCredentials(credential, secret, ref attempts),
                        OnPushStatusError = e => rejected = e.Message,
                    };
                    git.Network.Push(remote, $"refs/heads/{branch.Value}:refs/heads/{branch.Value}", pushOptions);
                    if (rejected is not null)
                    {
                        throw new GitEngineException($"The remote rejected the push of '{branch.Value}': {rejected}");
                    }
                }
                catch (LibGit2SharpException ex)
                {
                    // Drop the inner exception: native messages can echo URLs and headers.
                    throw new GitEngineException($"Push of '{branch.Value}' failed: {Scrub(ex.Message, secret)}");
                }
                catch (GitEngineException ex) when (ex.Message.Contains(secret, StringComparison.Ordinal))
                {
                    throw new GitEngineException(Scrub(ex.Message, secret));
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task FetchAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var root = _options.RepoRoot(repo);

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(root), cancellationToken).ConfigureAwait(false);
        using var credential = await _credentials.LeaseAsync(repo, GitOperation.Read, cancellationToken).ConfigureAwait(false);
        var secret = credential.Reveal();
        await Task.Run(
            () =>
            {
                try
                {
                    using var git = OpenRepository(root);
                    var remote = git.Network.Remotes[_options.RemoteName]
                        ?? throw new GitEngineException($"Remote '{_options.RemoteName}' is not configured.");
                    var attempts = 0;
                    var fetchOptions = new FetchOptions
                    {
                        CredentialsProvider = (_, _, _) => LeaseCredentials(credential, secret, ref attempts),
                    };
                    Commands.Fetch(git, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification), fetchOptions, null);
                }
                catch (LibGit2SharpException ex)
                {
                    throw new GitEngineException($"Fetch failed: {Scrub(ex.Message, secret)}");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiffEntry>> DiffAsync(RepoRef repo, string oldSha, string newSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ValidateSha(oldSha);
        ValidateSha(newSha);
        var root = _options.RepoRoot(repo);

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(root), cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var git = OpenRepository(root);
                var oldTree = git.Lookup<Commit>(oldSha)?.Tree ?? throw new GitEngineException($"Commit '{oldSha}' not found.");
                var newTree = git.Lookup<Commit>(newSha)?.Tree ?? throw new GitEngineException($"Commit '{newSha}' not found.");
                var changes = git.Diff.Compare<TreeChanges>(oldTree, newTree, new CompareOptions { Similarity = SimilarityOptions.Renames });
                return (IReadOnlyList<DiffEntry>)changes
                    .Where(c => c.Status != ChangeKind.Unmodified)
                    .Select(c => new DiffEntry(c.Path, StatusName(c.Status)))
                    .ToList();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var repos = await Task.Run(DiscoverRepoPaths, cancellationToken).ConfigureAwait(false);
        var errors = new List<Exception>();
        foreach (var path in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(path), cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() => RecoverRepo(path, errors), cancellationToken).ConfigureAwait(false);
            }
            catch (LibGit2SharpException ex)
            {
                errors.Add(new GitEngineException($"Recovery of '{path}' failed: {ex.Message}"));
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("Git recovery finished with errors.", errors);
        }
    }

    private const FileStatus _stagedFlags = FileStatus.NewInIndex | FileStatus.ModifiedInIndex | FileStatus.DeletedFromIndex
        | FileStatus.RenamedInIndex | FileStatus.TypeChangeInIndex;

    private static UsernamePasswordCredentials LeaseCredentials(GitCredential credential, string secret, ref int attempts)
    {
        // libgit2 calls again after a bad login; stop after one try instead of looping.
        if (Interlocked.Increment(ref attempts) > 1)
        {
            throw new GitEngineException("The remote rejected the credential.");
        }

        return new UsernamePasswordCredentials { Username = credential.Username, Password = secret };
    }

    private static string Scrub(string text, string secret) =>
        secret.Length == 0 ? text : text.Replace(secret, "***", StringComparison.Ordinal);

    private static Repository OpenRepository(string path) => new(path);

    private static Signature MakeSignature(GitSignatureIdentity identity) => new(identity.Name, identity.Email, DateTimeOffset.UtcNow);

    private static bool Covers(string listed, string statusPath) =>
        string.Equals(listed, statusPath, StringComparison.Ordinal)
        || statusPath.StartsWith(listed.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static string StatusName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Untracked => "added",
        ChangeKind.Deleted => "removed",
        ChangeKind.Renamed or ChangeKind.Copied => "renamed",
        _ => "modified",
    };

    private RepoRef ReadChecks(string workdir, BranchName branch)
    {
        if (!Repository.IsValid(workdir))
        {
            throw new GitEngineException($"'{workdir}' is not a git workspace.");
        }

        using var git = OpenRepository(workdir);
        var markerPath = Path.Combine(git.Info.Path, _repoMarkerFile);
        var marker = File.Exists(markerPath) ? File.ReadAllLines(markerPath) : [];
        if (marker.Length < 2 || marker[0].Length == 0 || marker[1].Length == 0)
        {
            throw new GitEngineException("The workspace was not created by the git engine, so its repository is unknown.");
        }

        var repoRef = new RepoRef(marker[0], marker[1]);
        if (_protectedBranches.Contains(branch.Value, StringComparer.OrdinalIgnoreCase))
        {
            throw new GitEngineException($"Pushing to '{branch.Value}' is refused: agents never push to a default branch.");
        }

        var defaultBranch = _options.DefaultBranch?.Invoke(repoRef) ?? RemoteDefaultBranch(git);
        if (defaultBranch is not null && string.Equals(defaultBranch, branch.Value, StringComparison.Ordinal))
        {
            throw new GitEngineException($"Pushing to the default branch '{branch.Value}' is refused.");
        }

        var local = git.Branches[branch.Value] ?? throw new GitEngineException($"Branch '{branch.Value}' does not exist in the workspace.");
        if (!git.Head.CanonicalName.Equals(local.CanonicalName, StringComparison.Ordinal))
        {
            throw new GitEngineException($"The workspace has '{git.Head.FriendlyName}' checked out, not '{branch.Value}'.");
        }

        return repoRef;
    }

    private string? RemoteDefaultBranch(Repository git)
    {
        var head = git.Refs[$"refs/remotes/{_options.RemoteName}/HEAD"];
        var prefix = $"refs/remotes/{_options.RemoteName}/";
        return head?.TargetIdentifier is { } target && target.StartsWith(prefix, StringComparison.Ordinal)
            ? target[prefix.Length..]
            : null;
    }

    private string WorktreePath(RepoRef repo, BranchName branch)
    {
        if (string.IsNullOrWhiteSpace(_options.WorktreeRoot))
        {
            throw new InvalidOperationException("GitEngineOptions.WorktreeRoot is not configured.");
        }

        var root = Path.GetFullPath(_options.WorktreeRoot);
        var segments = new List<string> { root, repo.Provider };
        segments.AddRange(repo.FullName.Split('/'));
        segments.AddRange(branch.Value.Split('/'));
        foreach (var segment in segments.Skip(1))
        {
            ValidateSegment(segment);
        }

        var full = Path.GetFullPath(Path.Combine([.. segments]));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new GitEngineException("The worktree path would leave the worktree root.");
        }

        return full;
    }

    private static void ValidateSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.Any(c => char.IsControl(c) || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw new GitEngineException($"'{segment}' is not a safe path segment.");
        }
    }

    /// <summary>Refuses branch names that are unsafe as refs or as path segments: traversal, absolute paths, a leading dash, control characters.</summary>
    internal static void ValidateBranch(string name)
    {
        var bad = string.IsNullOrWhiteSpace(name)
            || name.Length > _maxBranchLength
            || name[0] is '-' or '/' or '.'
            || name[^1] is '/' or '.'
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal)
            || name.Contains("@{", StringComparison.Ordinal)
            || name.Contains("/.", StringComparison.Ordinal)
            || name.EndsWith(".lock", StringComparison.Ordinal)
            || name.Contains(".lock/", StringComparison.Ordinal)
            || name is "@" or "HEAD"
            || name.Any(c => char.IsControl(c) || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\' or '\u007f')
            || Path.IsPathRooted(name);
        if (bad)
        {
            throw new GitEngineException($"'{name}' is not an acceptable branch name.");
        }
    }

    private static void ValidateSha(string sha)
    {
        if (string.IsNullOrEmpty(sha) || sha.Length is < 4 or > 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new GitEngineException($"'{sha}' is not a commit sha.");
        }
    }

    private static string RelativeInside(string workdir, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Any(c => char.IsControl(c) || c is '*' or '?' or '['))
        {
            throw new GitEngineException($"'{path}' is not a valid path inside the worktree.");
        }

        var full = Path.GetFullPath(Path.Combine(workdir, path));
        var prefix = workdir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new GitEngineException($"'{path}' escapes the worktree.");
        }

        var relative = Path.GetRelativePath(workdir, full).Replace('\\', '/');
        if (relative.Split('/').Any(s => string.Equals(s, ".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new GitEngineException($"'{path}' is inside git's own directory.");
        }

        return relative;
    }

    // Recovery.

    private List<string> DiscoverRepoPaths()
    {
        var found = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var repo in _options.KnownRepos())
        {
            var path = _options.RepoRoot(repo);
            found[RepoLocks.KeyForPath(path)] = path;
        }

        if (!string.IsNullOrWhiteSpace(_options.WorktreeRoot) && Directory.Exists(_options.WorktreeRoot))
        {
            var pending = new Stack<(string Dir, int Depth)>();
            pending.Push((Path.GetFullPath(_options.WorktreeRoot), 0));
            while (pending.Count > 0)
            {
                var (dir, depth) = pending.Pop();
                var marker = Path.Combine(dir, ".git");
                if (File.Exists(marker))
                {
                    if (MainRepoOf(dir, marker) is { } main)
                    {
                        found[RepoLocks.KeyForPath(main)] = main;
                    }

                    continue;
                }

                if (depth >= _maxWorktreeScanDepth)
                {
                    continue;
                }

                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return [.. found.Values];
    }

    // A worktree's .git file reads "gitdir: <main>/.git/worktrees/<name>".
    private static string? MainRepoOf(string worktreeDir, string gitFile)
    {
        var line = File.ReadLines(gitFile).FirstOrDefault();
        const string prefix = "gitdir:";
        if (line is null || !line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var admin = RepoLocks.KeyForPath(Path.GetFullPath(line[prefix.Length..].Trim(), worktreeDir));
        var worktrees = Directory.GetParent(admin);
        if (worktrees is null || !string.Equals(worktrees.Name, "worktrees", StringComparison.Ordinal) || worktrees.Parent is null)
        {
            return null;
        }

        var gitDir = worktrees.Parent;
        return string.Equals(gitDir.Name, ".git", StringComparison.Ordinal) && gitDir.Parent is not null
            ? gitDir.Parent.FullName
            : gitDir.FullName;
    }

    private static void RecoverRepo(string path, List<Exception> errors)
    {
        if (!Repository.IsValid(path))
        {
            return;
        }

        using var git = OpenRepository(path);
        AbortMerge(git);

        var worktreesDir = Path.Combine(git.Info.Path, "worktrees");
        if (!Directory.Exists(worktreesDir))
        {
            return;
        }

        foreach (var admin in Directory.EnumerateDirectories(worktreesDir).ToList())
        {
            try
            {
                if (File.Exists(Path.Combine(admin, "locked")))
                {
                    continue;
                }

                var gitdirFile = Path.Combine(admin, "gitdir");
                var target = File.Exists(gitdirFile) ? File.ReadAllText(gitdirFile).Trim() : string.Empty;
                if (target.Length == 0 || !File.Exists(target))
                {
                    // The worktree directory is gone: prune its admin entry, as `git worktree prune` does.
                    Directory.Delete(admin, recursive: true);
                    continue;
                }

                var workdir = Path.GetDirectoryName(target)!;
                if (Repository.IsValid(workdir))
                {
                    using var worktreeRepo = OpenRepository(workdir);
                    AbortMerge(worktreeRepo);
                }
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new GitEngineException($"Recovery of worktree entry '{Path.GetFileName(admin)}' failed: {ex.Message}"));
            }
        }
    }

    private static void AbortMerge(Repository git)
    {
        var stateDir = git.Info.Path;
        if (!File.Exists(Path.Combine(stateDir, "MERGE_HEAD")))
        {
            return;
        }

        if (!git.Info.IsBare && git.Head.Tip is not null)
        {
            git.Reset(ResetMode.Hard);
        }

        foreach (var name in _mergeStateNames)
        {
            var file = Path.Combine(stateDir, name);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
