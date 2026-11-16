namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// The only route to a squash merge. It runs three checks in order and refuses on the first failure, before any merge call:
/// the approval and the merge name the same head sha; the owner's signature verifies; and the PR's head is still that sha.
/// A moved head voids the approval, and the owner must approve again (doc 21).
/// </summary>
internal sealed class OwnerSignedMerge
{
    private readonly IGitHubAdapter _adapter;
    private readonly IGitHubMergeExecutor _executor;
    private readonly IOwnerApprovalVerifier _verifier;

    public OwnerSignedMerge(IGitHubAdapter adapter, IGitHubMergeExecutor executor, IOwnerApprovalVerifier verifier)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public async Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(merge);

        if (!string.Equals(approval.HeadSha, merge.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the approval and the merge name different head shas");

        if (!await _verifier.VerifyAsync(approval, cancellationToken))
            throw new MergeNotApprovedException("the owner's signature did not verify");

        var current = await _adapter.GetHeadShaAsync(approval.Pr, cancellationToken);
        if (!string.Equals(current, approval.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the PR head moved after approval, so the owner must approve again");

        await _executor.SquashMergeAsync(approval.Pr, merge, cancellationToken);
    }
}
