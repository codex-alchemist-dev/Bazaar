using Bazaar.Approvals;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// The only route to a squash merge. It runs these checks in order and refuses on the first failure, before any merge call:
/// the approval and the merge name the same head sha; the approval is for this merge; the owner's signature verifies through
/// <see cref="IOwnerSignatureVerifier"/>; the PR's head is still that sha; and every check on that sha passes. A moved head voids
/// the approval, and the owner must approve again (doc 21, doc 32 section 5a).
/// </summary>
internal sealed class OwnerSignedMerge : IOwnerSignedMergeService
{
    private readonly IGitHubAdapter _adapter;
    private readonly IGitHubMergeExecutor _executor;
    private readonly IOwnerSignatureVerifier _verifier;
    private readonly TimeProvider _time;

    public OwnerSignedMerge(IGitHubAdapter adapter, IGitHubMergeExecutor executor, IOwnerSignatureVerifier verifier, TimeProvider? time = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _time = time ?? TimeProvider.System;
    }

    public async Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(merge);

        var payload = approval.Payload;
        if (!string.Equals(payload.HeadSha, merge.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the approval and the merge name different head shas");

        if (payload.Action != OwnerMergeArgs.Action)
            throw new MergeNotApprovedException("the approval is not for a squash merge");

        if (!payload.ArgsHash.AsSpan().SequenceEqual(OwnerMergeArgs.Hash(approval.Pr, merge)))
            throw new MergeNotApprovedException("the approval is for a different merge");

        var verdict = await _verifier.VerifyAsync(payload, approval.Signature, _time.GetUtcNow(), cancellationToken);
        if (verdict != OwnerSignatureVerdict.Valid)
            throw new MergeNotApprovedException($"the owner's signature was refused ({verdict})");

        var current = await _adapter.GetHeadShaAsync(approval.Pr, cancellationToken);
        if (!string.Equals(current, payload.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the PR head moved after approval, so the owner must approve again");

        // Checks are read for the approved sha, so a check that turns red after approval stops the merge too (doc 21, Merge request path).
        var checks = await _adapter.GetChecksStateAsync(approval.Pr, payload.HeadSha!, cancellationToken);
        if (checks != PrChecksState.Passing)
            throw new MergeNotApprovedException($"the checks on the approved head are not passing ({checks})");

        await _executor.SquashMergeAsync(approval.Pr, merge, cancellationToken);
    }
}
