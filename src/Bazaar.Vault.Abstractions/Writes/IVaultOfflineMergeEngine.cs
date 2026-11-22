namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// One device's offline Vault commit, as the Vault merge engine receives it (doc 56 section 8, item 3). The repository is
/// not part of the request: the host picks the engine per repository. The target is always the Vault main head.
/// </summary>
/// <param name="WriteId">The offline write that carried the commit (idempotency key, <c>Bazaar-Offline-Write</c> trailer).</param>
/// <param name="DeviceId">The device that made the commit (<c>Bazaar-Device</c> trailer).</param>
/// <param name="Branch">The branch the device pushed (<c>Bazaar-Offline-Branch</c> trailer).</param>
/// <param name="HeadCommit">The commit the device wants to land; the branch must point at it.</param>
/// <param name="BaseCommit">The main-line commit the device started from (<c>Bazaar-Edit-Base</c> trailer).</param>
/// <param name="At">When the intent reached the server; names the conflict branch.</param>
public sealed record VaultOfflineMergeRequest(
    Guid WriteId,
    Guid DeviceId,
    string Branch,
    string HeadCommit,
    string BaseCommit,
    DateTimeOffset At);

/// <summary>What the Vault merge engine did with an offline commit.</summary>
public abstract record VaultOfflineMergeResult
{
    private protected VaultOfflineMergeResult()
    {
    }

    /// <summary>The commit landed on main (or was already contained in it).</summary>
    /// <param name="NewHead">The Vault head after the merge.</param>
    public sealed record Merged(string NewHead) : VaultOfflineMergeResult;

    /// <summary>
    /// Overlapping edits. Main is untouched, and the device's commit is kept on <paramref name="BranchName"/>, so both
    /// versions survive and nothing is lost.
    /// </summary>
    /// <param name="BranchName">The kept branch, <c>conflict/&lt;device&gt;/&lt;timestamp&gt;</c>.</param>
    /// <param name="ConflictedPaths">The paths both sides changed, ordinal-sorted.</param>
    public sealed record Conflicted(string BranchName, IReadOnlyList<string> ConflictedPaths) : VaultOfflineMergeResult;

    /// <summary>The engine refused the commit; nothing changed.</summary>
    /// <param name="Reason">Why it was refused.</param>
    public sealed record Rejected(string Reason) : VaultOfflineMergeResult;
}

/// <summary>
/// The Vault module's seam for landing an offline commit (doc 56 section 8, item 3). It runs through the single Vault
/// write path, one merge at a time per Vault, and never loses an edit: a conflict keeps the device's commit on a branch.
/// </summary>
public interface IVaultOfflineMergeEngine
{
    /// <summary>Lands or refuses one offline commit.</summary>
    /// <param name="request">The offline commit.</param>
    /// <param name="ct">Cancels the merge.</param>
    ValueTask<VaultOfflineMergeResult> MergeAsync(VaultOfflineMergeRequest request, CancellationToken ct);
}
