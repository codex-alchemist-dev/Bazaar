using System.Collections.Concurrent;

namespace Bazaar.Server.CodingGit.Fakes;

/// <summary>In-memory <see cref="ICodingGitEngine"/> for tests. It records worktrees and commits and runs no git.</summary>
public sealed class InMemoryCodingGitEngine : ICodingGitEngine
{
    private readonly ConcurrentDictionary<string, FakeWorktree> _worktrees = new(StringComparer.Ordinal);
    private int _commitCounter;

    /// <summary>Commits made so far, in order.</summary>
    public IReadOnlyList<EngineCommitRequest> Commits
    {
        get
        {
            lock (_commitLog)
            {
                return [.. _commitLog];
            }
        }
    }

    private readonly List<EngineCommitRequest> _commitLog = [];

    /// <summary>The paths of worktrees added so far.</summary>
    public IReadOnlyCollection<string> WorktreePaths => [.. _worktrees.Keys];

    /// <summary>The branch checked out at a worktree path, or null.</summary>
    public BranchName? BranchAt(string path) => _worktrees.TryGetValue(path, out var wt) ? wt.Branch : null;

    /// <summary>Records a pending change so the next commit and diff see it.</summary>
    public void StageChange(string worktreePath, string relativePath, string patch)
    {
        var wt = _worktrees[worktreePath];
        lock (wt)
        {
            wt.Pending.Add((relativePath, patch));
        }
    }

    /// <summary>An exception the next engine call throws once, then clears. For failure-path tests.</summary>
    public Exception? FailNextWith
    {
        get => Volatile.Read(ref _failNext);
        set => Volatile.Write(ref _failNext, value);
    }

    /// <summary>A gate the engine awaits inside each call, so tests can hold an operation open.</summary>
    public Task? Gate { get; set; }

    /// <inheritdoc />
    public async Task AddWorktreeAsync(AddWorktreeRequest request, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        var existing = _worktrees.GetOrAdd(request.Path, _ => new FakeWorktree(request.Repo, request.Branch));
        if (existing.Branch != request.Branch)
        {
            throw new InvalidOperationException("The path already holds a worktree on another branch.");
        }
    }

    /// <inheritdoc />
    public async Task<CommitId> CommitAsync(EngineCommitRequest request, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        var wt = Find(request.WorktreePath);
        lock (wt)
        {
            if (wt.Pending.Count == 0)
            {
                throw new CodingGitException(CodingGitError.NothingToCommit, "The worktree has no changes to commit.");
            }

            wt.Committed.AddRange(wt.Pending);
            wt.Pending.Clear();
        }

        lock (_commitLog)
        {
            _commitLog.Add(request);
        }

        return new CommitId($"fake{Interlocked.Increment(ref _commitCounter):x8}");
    }

    /// <inheritdoc />
    public async Task<GitDiff> DiffAgainstBaseAsync(string worktreePath, string baseBranch, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        var wt = Find(worktreePath);
        lock (wt)
        {
            var all = wt.Committed.Concat(wt.Pending).ToList();
            return new GitDiff(baseBranch, [.. all.Select(c => c.Path).Distinct(StringComparer.Ordinal)], string.Concat(all.Select(c => c.Patch)));
        }
    }

    private FakeWorktree Find(string path) =>
        _worktrees.TryGetValue(path, out var wt) ? wt : throw new InvalidOperationException("No worktree at that path.");

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fail = Interlocked.Exchange(ref _failNext, null);
        if (fail is not null)
        {
            throw fail;
        }

        if (Gate is { } gate)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Exception? _failNext;

    private sealed class FakeWorktree(RepoRef repo, BranchName branch)
    {
        public RepoRef Repo { get; } = repo;

        public BranchName Branch { get; } = branch;

        public List<(string Path, string Patch)> Pending { get; } = [];

        public List<(string Path, string Patch)> Committed { get; } = [];
    }
}
