using Bazaar.Approvals;
using Bazaar.Contracts.GitHub;
using Bazaar.GitHub.Wire;

namespace Bazaar.GitHub.Adapter;

/// <summary>
/// The only route to a squash merge. It runs these checks in order and refuses on the first failure, before any merge call:
/// the approval and the merge name the same head sha; the approval is for this merge; the owner's signature verifies through
/// <see cref="IOwnerSignatureVerifier"/>; and the PR's head is still that sha. A moved head voids the approval, and the owner
/// must approve again (doc 21, doc 32 section 5a). The head and checks-must-be-green gates run before the signature is
/// verified, so a stale or red PR does not spend the owner's single-use nonce. Every caller of a merge gets them here.
/// </summary>
internal sealed class OwnerSignedMerge
{
    private readonly IGitHubAdapter _adapter;
    private readonly IGitHubMergeExecutor _executor;
    private readonly IGitHubPrReadModel _reads;
    private readonly IOwnerSignatureVerifier _verifier;
    private readonly TimeProvider _time;

    public OwnerSignedMerge(IGitHubAdapter adapter, IGitHubMergeExecutor executor, IGitHubPrReadModel reads, IOwnerSignatureVerifier verifier, TimeProvider? time = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _time = time ?? TimeProvider.System;
    }

    public async Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(merge);

        var payload = approval.Payload;
        if (!string.Equals(payload.HeadSha, merge.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the approval and the merge name different head shas", MergeRefusalReason.HeadMismatch);

        if (payload.Action != OwnerMergeArgs.Action)
            throw new MergeNotApprovedException("the approval is not for a squash merge", MergeRefusalReason.WrongAction);

        if (!payload.ArgsHash.AsSpan().SequenceEqual(OwnerMergeArgs.Hash(approval.Pr, merge)))
            throw new MergeNotApprovedException("the approval is for a different merge", MergeRefusalReason.DifferentMerge);

        var current = await _adapter.GetHeadShaAsync(approval.Pr, cancellationToken);
        if (!string.Equals(current, payload.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new MergeNotApprovedException("the PR head moved after approval, so the owner must approve again", MergeRefusalReason.HeadMoved);

        var checks = await _reads.GetChecksAsync(approval.Pr, merge.HeadSha, cancellationToken);
        if (checks != PrChecksRollup.Passing)
            throw new MergeNotApprovedException($"checks are {checks.ToString().ToLowerInvariant()}", MergeRefusalReason.ChecksNotPassing, checks: checks);

        var verdict = await _verifier.VerifyAsync(payload, approval.Signature, _time.GetUtcNow(), cancellationToken);
        if (verdict != OwnerSignatureVerdict.Valid)
            throw new MergeNotApprovedException($"the owner's signature was refused ({verdict})", MergeRefusalReason.SignatureRejected, verdict);

        await _executor.SquashMergeAsync(approval.Pr, merge, cancellationToken);
    }
}
