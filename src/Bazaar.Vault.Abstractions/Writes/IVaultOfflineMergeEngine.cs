namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// The Vault side of an offline push (doc 18, "Offline merge"; doc 49 section 4 item 2). A device that edited offline
/// pushes its commits; the engine merges them into the main branch with a three-way merge, or keeps them on a
/// <c>conflict/&lt;device&gt;/&lt;timestamp&gt;</c> branch when hunks overlap.
/// </summary>
/// <remarks>
/// This is a Vault-owned seam: the Vault module does not reference Offline Sync. The host adapts it to Offline Sync's
/// merger contract and opens the review item for a conflict; the engine creates the conflict branch only.
/// </remarks>
public interface IVaultOfflineMergeEngine
{
    Task<VaultOfflineMergeResult> MergeAsync(VaultOfflineMergeRequest request, CancellationToken ct);
}

/// <summary>One offline push. The pushed commits must already be in the Vault repository's object database.</summary>
/// <param name="DeviceId">The pushing device (used in the conflict branch name and the <c>Bazaar-Device</c> trailer).</param>
/// <param name="Branch">The target branch: the branch of the main checkout, with or without <c>refs/heads/</c>.</param>
/// <param name="HeadCommit">The full SHA of the device's head commit.</param>
/// <param name="BaseCommit">The full SHA of the main-branch commit the device last synced.</param>
public sealed record VaultOfflineMergeRequest(string DeviceId, string Branch, string HeadCommit, string BaseCommit);

/// <summary>What an offline push did. Exactly one of the nested records.</summary>
public abstract record VaultOfflineMergeResult
{
    private VaultOfflineMergeResult()
    {
    }

    /// <summary>The push is on the main branch. <paramref name="NewHead"/> is the main head after the merge.</summary>
    public sealed record Merged(string NewHead) : VaultOfflineMergeResult;

    /// <summary>
    /// Hunks overlapped. Main is untouched; the pushed head is on <paramref name="BranchName"/>, and
    /// <paramref name="ConflictedPaths"/> lists the conflicting paths, ordinal-sorted.
    /// </summary>
    public sealed record Conflicted(string BranchName, IReadOnlyList<string> ConflictedPaths) : VaultOfflineMergeResult;

    /// <summary>The push cannot be merged or kept (unknown commit, unrelated base, unsafe path, frozen decision).</summary>
    public sealed record Rejected(string Reason) : VaultOfflineMergeResult;
}
