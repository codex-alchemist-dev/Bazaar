using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.GitHub.Tests.Workspace;

/// <summary>
/// An <see cref="IGitProvider"/> that records calls and tracks how many are in flight at once. Only the worktree and
/// commit members are used by the workspace tests; the rest are not reached.
/// </summary>
internal sealed class FakeGitProvider : IGitProvider
{
    private readonly object _gate = new();
    private int _active;

    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>The highest number of provider calls that ran at the same time.</summary>
    public int MaxActive { get; private set; }

    public int CommitCount { get; private set; }

    public List<(string CheckoutPath, string Message, GitIdentity Author)> Commits { get; } = [];

    public async Task<string> AddWorktreeAsync(string repoPath, string branch, string worktreePath, CancellationToken ct)
    {
        await TrackAsync(ct).ConfigureAwait(false);
        return Path.GetFullPath(worktreePath);
    }

    public async Task<string> CommitAsync(string checkoutPath, string message, GitIdentity author, CancellationToken ct)
    {
        await TrackAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            CommitCount++;
            Commits.Add((checkoutPath, message, author));
        }

        return $"fake-sha-{CommitCount}";
    }

    public Task<IReadOnlyList<WorktreeInfo>> ListWorktreesAsync(string repoPath, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<string>> PruneWorktreesAsync(string repoPath, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<MergeResult> MergeAsync(string checkoutPath, string branch, GitIdentity author, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task AbortMergeAsync(string checkoutPath, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<DiffEntry>> DiffAsync(string repoPath, string oldSha, string newSha, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RecoveryReport> RecoverAsync(string repoPath, CancellationToken ct) => throw new NotSupportedException();

    private async Task TrackAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _active++;
            MaxActive = Math.Max(MaxActive, _active);
        }

        try
        {
            await Task.Delay(Delay, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _active--;
            }
        }
    }
}
