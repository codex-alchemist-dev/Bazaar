using Bazaar.Approvals;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.GitHub.Wire;

/// <summary>
/// <c>pr.merge</c> (doc 21, Merge request path; doc 32 section 5a; C25): builds the <see cref="OwnerMergeApproval"/> from the
/// request's owner payload and signature and runs it through <see cref="OwnerSignedMerge"/>, the only route to a squash merge.
/// No verifier lives here. Before the signature is spent, the handler checks the head is still the approved sha (reply
/// <c>stale</c>) and that checks pass (reply <c>checks_failed</c>), so a stale or red PR does not burn the owner's nonce.
/// Owner principals only; an agent never reaches it.
/// </summary>
public sealed class PrMergeWireHandler : IWireRequestHandler
{
    private readonly IGitHubAdapter _adapter;
    private readonly IGitHubPrReadModel _reads;
    private readonly OwnerSignedMerge _gate;
    private readonly TimeProvider _time;

    internal PrMergeWireHandler(IGitHubAdapter adapter, IGitHubPrReadModel reads, OwnerSignedMerge gate, TimeProvider? time = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Wires the handler over the adapter that also performs the merge: the one object that implements both the read surface
    /// and the internal merge executor.
    /// </summary>
    public PrMergeWireHandler(GitHubAdapter adapter, IGitHubPrReadModel reads, IOwnerSignatureVerifier verifier, TimeProvider? time = null)
        : this(adapter, reads, new OwnerSignedMerge(adapter, adapter, verifier, time), time)
    {
    }

    /// <inheritdoc />
    public string Type => WireTypes.PrMerge;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PrWireSupport.IsOwner(context))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (!PrWireSupport.TryDecode<PrMergeRequest>(context, out var request, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.PrId) || string.IsNullOrWhiteSpace(request.HeadSha)
            || string.IsNullOrWhiteSpace(request.CommitTitle) || request.Owner is null || request.Signature is null
            || request.Owner.Action is null || request.Owner.ArgsHash is null || request.Owner.Nonce is null || request.Owner.DeviceId is null)
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A merge request needs a PR id, head sha, title, owner payload and signature.");
        }

        try
        {
            var pr = await _reads.ResolveAsync(request.PrId, cancellationToken).ConfigureAwait(false);
            if (pr is null)
            {
                return WireReply.Fail(WireErrorCodes.NotFound);
            }

            var merge = new MergeRequest(request.HeadSha, request.CommitTitle, request.CommitMessage ?? string.Empty);
            var approval = new OwnerMergeApproval(pr, ToPayload(request.Owner), request.Signature);

            // Cheap refusals first. None of them spends the owner's single-use nonce.
            var current = await _adapter.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return Reply(context, PrMergeStatus.Stale, current, "The PR head moved. Approve again.");
            }

            var checks = await _reads.GetChecksAsync(pr, request.HeadSha, cancellationToken).ConfigureAwait(false);
            if (checks != PrChecksRollup.Passing)
            {
                return Reply(context, PrMergeStatus.ChecksFailed, current, $"Checks are {checks.ToString().ToLowerInvariant()}.");
            }

            try
            {
                await _gate.MergeAsync(approval, merge, cancellationToken).ConfigureAwait(false);
            }
            catch (MergeNotApprovedException refused)
            {
                return await MapRefusalAsync(context, refused, pr, cancellationToken).ConfigureAwait(false);
            }

            return Reply(context, PrMergeStatus.Merged, current, null);
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }

    private async Task<WireReply> MapRefusalAsync(WireRequestContext context, MergeNotApprovedException refused, PrRef pr, CancellationToken ct)
    {
        switch (refused.Reason)
        {
            case MergeRefusalReason.SignatureRejected:
                return Reply(context, PrMergeStatus.BadSignature, null, $"The owner signature was refused ({refused.Verdict}).");
            case MergeRefusalReason.HeadMoved:
                var now = await _adapter.GetHeadShaAsync(pr, ct).ConfigureAwait(false);
                return Reply(context, PrMergeStatus.Stale, now, "The PR head moved. Approve again.");
            default:
                return Reply(context, PrMergeStatus.Refused, null, refused.Message);
        }
    }

    private static OwnerPayload ToPayload(PrOwnerPayload p) =>
        new(p.Action, p.ApprovalId, p.ArgsHash, p.HeadSha, p.HumanId, p.DeviceId, p.IssuedAt, p.ExpiresAt, p.Nonce);

    private static WireReply Reply(WireRequestContext context, PrMergeStatus status, string? head, string? message) =>
        WireReply.Ok(context.Connection.Codec, new PrMergeReply(status, head, message));
}
