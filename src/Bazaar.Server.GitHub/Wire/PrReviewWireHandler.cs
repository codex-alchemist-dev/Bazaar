using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.GitHub.Wire;

/// <summary>
/// <c>pr.review</c> (doc 21, Client PR wire): a comment or request-changes review against one head sha. <c>approve</c> and any
/// other event is <c>refused</c> before GitHub is touched: reviewer approval is data, not merge authority. A moved head is
/// <c>stale</c> and nothing is posted. Owner only.
/// </summary>
public sealed class PrReviewWireHandler(IGitHubAdapter adapter, IGitHubPrReadModel reads, TimeProvider? time = null) : IWireRequestHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public string Type => WireTypes.PrReview;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PrWireSupport.IsOwner(context))
        {
            return WireReply.Fail(WireErrorCodes.NotFound);
        }

        if (!PrWireSupport.TryDecode<PrReviewRequest>(context, out var request, out var failure))
        {
            return failure!;
        }

        if (string.IsNullOrWhiteSpace(request.PrId) || string.IsNullOrWhiteSpace(request.HeadSha))
        {
            return WireReply.Fail(WireErrorCodes.BadRequest, "A review request needs a PR id and a head sha.");
        }

        string? githubEvent = request.Event switch
        {
            PrReviewEvents.Comment => "COMMENT",
            PrReviewEvents.RequestChanges => "REQUEST_CHANGES",
            _ => null,
        };
        if (githubEvent is null)
        {
            return Reply(context, PrReviewStatus.Refused, null, "Only comment and request_changes are accepted. Approval is not a merge authority.");
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Reply(context, PrReviewStatus.Refused, null, "A review needs a body.");
        }

        try
        {
            var pr = await reads.ResolveAsync(request.PrId, cancellationToken).ConfigureAwait(false);
            if (pr is null)
            {
                return WireReply.Fail(WireErrorCodes.NotFound);
            }

            var current = await adapter.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, request.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return Reply(context, PrReviewStatus.Stale, current, null);
            }

            await adapter.PostReviewAsync(pr, new ReviewRequest(request.HeadSha, githubEvent, request.Body), cancellationToken).ConfigureAwait(false);
            return Reply(context, PrReviewStatus.Applied, current, null);
        }
        catch (GitHubApiException error)
        {
            return PrWireSupport.MapGitHubFailure(error, _time);
        }
    }

    private static WireReply Reply(WireRequestContext context, PrReviewStatus status, string? head, string? message) =>
        WireReply.Ok(context.Connection.Codec, new PrReviewReply(status, head, message));
}
