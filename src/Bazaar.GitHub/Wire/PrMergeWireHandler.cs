using Bazaar.Approvals;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.GitHub.Wire;

/// <summary>
/// <c>pr.merge</c> (doc 21, Merge request path; doc 32 section 5a; C25): builds the <see cref="OwnerMergeApproval"/> from the
/// request's owner payload and signature and runs it through <see cref="OwnerSignedMerge"/>, the only route to a squash merge.
/// No verifier and no gate live here: the head and checks gates are in <see cref="OwnerSignedMerge"/> and run before the
/// owner's nonce is spent. The handler maps refusals to replies (<c>stale</c>, <c>checks_failed</c>, <c>bad_signature</c>,
/// <c>refused</c>) and GitHub's own merge refusals to <c>not_mergeable</c> (405, 422) and <c>conflict</c> (409).
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

            // The head and checks gates live in OwnerSignedMerge and run before the owner's nonce is spent.
            try
            {
                await _gate.MergeAsync(approval, merge, cancellationToken).ConfigureAwait(false);
            }
            catch (MergeNotApprovedException refused)
            {
                return await MapRefusalAsync(context, refused, pr, request.HeadSha, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubApiException error) when (MapMergeFailure(context, error) is { } mapped)
            {
                return mapped;
            }

            return Reply(context, PrMergeStatus.Merged, request.HeadSha, null);
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }

    private async Task<WireReply> MapRefusalAsync(WireRequestContext context, MergeNotApprovedException refused, PrRef pr, string head, CancellationToken ct)
    {
        switch (refused.Reason)
        {
            case MergeRefusalReason.SignatureRejected:
                return Reply(context, PrMergeStatus.BadSignature, null, $"The owner signature was refused ({refused.Verdict}).");
            case MergeRefusalReason.ChecksNotPassing:
                return Reply(context, PrMergeStatus.ChecksFailed, head, $"Checks are {refused.Checks.ToString()!.ToLowerInvariant()}.");
            case MergeRefusalReason.HeadMoved:
                var now = await _adapter.GetHeadShaAsync(pr, ct).ConfigureAwait(false);
                return Reply(context, PrMergeStatus.Stale, now, "The PR head moved. Approve again.");
            default:
                return Reply(context, PrMergeStatus.Refused, null, refused.Message);
        }
    }

    /// <summary>
    /// Maps GitHub's own refusal of the merge call (not a rate limit) to its reply code, so the owner learns the real reason.
    /// Null for anything else, which falls through to the generic failure mapping.
    /// </summary>
    private static WireReply? MapMergeFailure(WireRequestContext context, GitHubApiException error)
    {
        if (error is GitHubRateLimitedException)
        {
            return null;
        }

        var detail = string.IsNullOrWhiteSpace(error.GitHubMessage) ? null : $" ({error.GitHubMessage})";
        return error.StatusCode switch
        {
            System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.UnprocessableEntity =>
                Reply(context, PrMergeStatus.NotMergeable, null, "GitHub says the PR is not mergeable" + detail + "."),
            System.Net.HttpStatusCode.Conflict =>
                Reply(context, PrMergeStatus.Conflict, null, "GitHub reports a merge conflict or a moved head" + detail + "."),
            _ => null,
        };
    }

    private static OwnerPayload ToPayload(PrOwnerPayload p) =>
        new(p.Action, p.ApprovalId, p.ArgsHash, p.HeadSha, p.HumanId, p.DeviceId, p.IssuedAt, p.ExpiresAt, p.Nonce);

    private static WireReply Reply(WireRequestContext context, PrMergeStatus status, string? head, string? message) =>
        WireReply.Ok(context.Connection.Codec, new PrMergeReply(status, head, message));
}
