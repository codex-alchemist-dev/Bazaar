using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// Posts a conflict card into a GC. The GC card API is not in the codebase yet, so the Vault side depends on this seam;
/// the real GC poster is wired in when that API lands.
/// </summary>
public interface IGcCardPoster
{
    /// <summary>Posts <paramref name="card"/> into <paramref name="gcId"/>, attached to <paramref name="taskId"/> when set.</summary>
    Task PostConflictCardAsync(string gcId, string? taskId, VaultConflictCard card, CancellationToken ct);
}

/// <summary>
/// Builds the conflict card for a refused merge and posts it to the task's GC (doc 18, Write path step 5). Only a merge
/// that was aborted for conflicts is posted; anything else returns false and posts nothing.
/// </summary>
public sealed class VaultConflictCardPublisher(IGitProvider git, IGcCardPoster poster)
{
    /// <summary>
    /// Posts a card for <paramref name="result"/> when it is a conflict. <paramref name="baseCommit"/> is the commit the
    /// agent branched from (the submission's base); the result's old commit is the main line the merge was attempted on.
    /// Returns true when a card was posted.
    /// </summary>
    public async Task<bool> PublishAsync(
        VaultEditResult result,
        string repoPath,
        string baseCommit,
        string branch,
        string gcId,
        string? taskId,
        IReadOnlyList<string>? actions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(gcId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);

        if (result.Outcome != VaultEditOutcome.Conflicted)
        {
            return false;
        }

        var card = await VaultConflictCards.BuildAsync(
            git, repoPath, baseCommit, result.OldCommit, branch, result.ConflictPaths, actions, ct);
        await poster.PostConflictCardAsync(gcId, taskId, card, ct);
        return true;
    }
}
