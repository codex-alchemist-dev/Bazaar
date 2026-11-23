using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Tests.Constellation.GitDag;

/// <summary>
/// An in-memory commit DAG behind <see cref="IGitProvider"/>, for the Git DAG indexer tests. Only commit listing and
/// commit resolution are implemented. The other members throw, so a test that reaches them fails loudly.
/// </summary>
internal sealed class InMemoryGitDagProvider : IGitProvider
{
    private readonly Dictionary<string, FakeCommit> _commits = new(StringComparer.Ordinal);
    private readonly string? _head;
    private readonly bool _reverseListOrder;

    /// <param name="commits">The commits of the DAG. Every parent must also be listed here.</param>
    /// <param name="head">The commit that "HEAD" resolves to. Null for a repository with no commits.</param>
    /// <param name="reverseListOrder">Lists commits newest first instead of in insertion order, to vary the input.</param>
    public InMemoryGitDagProvider(IEnumerable<FakeCommit> commits, string? head = null, bool reverseListOrder = false)
    {
        foreach (var commit in commits)
        {
            _commits.Add(commit.Sha, commit);
        }

        _head = head;
        _reverseListOrder = reverseListOrder;
    }

    /// <summary>A commit in the in-memory DAG.</summary>
    public sealed record FakeCommit(string Sha, string[] Parents, DateTimeOffset CommittedAt);

    public Task<string> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (commitish == "HEAD" && _head is not null)
        {
            return Task.FromResult(_head);
        }

        if (_commits.ContainsKey(commitish))
        {
            return Task.FromResult(commitish);
        }

        throw new InvalidOperationException($"Commit-ish '{commitish}' does not resolve.");
    }

    public Task<IReadOnlyList<GitCommitInfo>> ListCommitsAsync(string repoPath, string? excludeSha, string includeSha, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var excluded = excludeSha is null ? new HashSet<string>(StringComparer.Ordinal) : Ancestors(excludeSha);
        var listed = new List<GitCommitInfo>();
        foreach (var sha in Ancestors(includeSha))
        {
            if (excluded.Contains(sha))
            {
                continue;
            }

            var commit = _commits[sha];
            listed.Add(new GitCommitInfo(sha, commit.Parents, "Fake Author", commit.CommittedAt, "message", []));
        }

        var ordered = _reverseListOrder ? listed.AsEnumerable().Reverse().ToList() : listed;
        return Task.FromResult<IReadOnlyList<GitCommitInfo>>(ordered);
    }

    /// <summary>Every commit reachable from <paramref name="sha"/>, including itself.</summary>
    private HashSet<string> Ancestors(string sha)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(sha);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var parent in _commits[current].Parents)
            {
                pending.Push(parent);
            }
        }

        return seen;
    }

    public Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct) => throw new NotSupportedException();

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct, string? message = null) => throw new NotSupportedException();

    public Task<string> HeadAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, string commitSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> ReadTextAsync(string repoPath, string commitSha, string path, CancellationToken ct) => throw new NotSupportedException();

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();
}
