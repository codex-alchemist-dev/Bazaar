using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Hooks for <see cref="VaultOfflineMergeEngine"/> (doc 18, "Offline merge"). Offline merges land through the same vault
/// lane, identity and commit observer as every other writer commit, so they never race a submission.
/// </summary>
public sealed partial class VaultWriter
{
    internal IGitProvider Git => _git;

    internal string RepoPath => _repoPath;

    internal GitIdentity Identity => _writer;

    /// <summary>Runs <paramref name="work"/> while holding the vault lane.</summary>
    internal async Task<T> InVaultLaneAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var laneLease = await AcquireAsync(_vaultLane, ct);
        return await work();
    }

    /// <summary>Tells the commit observer about a commit landed by the offline merge.</summary>
    internal Task NotifyCommitAsync(string oldCommit, string newCommit, CancellationToken ct) => NotifyAsync(oldCommit, newCommit, ct);
}
