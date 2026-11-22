using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>
/// Applies a grant change the owner has approved. The approvals module calls it once, after the approval is recorded. A grant
/// goes through the store's own checks; a revoke goes through <see cref="HiveCopyService.RevokeAsync"/>, so held copies are
/// dropped and written copies are deprecated, never deleted.
/// </summary>
public sealed class HiveGrantApplier(IHiveGrantStore grants, HiveCopyService copies)
{
    /// <summary>Applies the change on behalf of <paramref name="approvedBy"/>; returns the revoke result, or null for a grant.</summary>
    public async Task<HiveRevokeResult?> ApplyApprovedAsync(HiveGrantChange change, string approvedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        switch (change.Action)
        {
            case HiveGrantAction.Set:
                await grants.AddAsync(new HiveGrant(change.FromProject, change.ToProject, change.Kind, approvedBy), ct).ConfigureAwait(false);
                return null;
            case HiveGrantAction.Revoke:
                return await copies.RevokeAsync(change.FromProject, change.ToProject, change.Kind, approvedBy, ct).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Action, "Unknown grant action.");
        }
    }
}
