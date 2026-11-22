using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The PR wire over the authenticated socket (doc 21, Client PR wire). It sends the five <c>pr.*</c> requests through
/// <see cref="IWireRequester"/>, the same way the other feature modules do. It refuses any review event other than comment or
/// request-changes before anything is sent, so an approve never reaches the wire from this client.
/// </summary>
public sealed class WirePullRequestClient(IWireRequester requests) : IPullRequestClient
{
    /// <inheritdoc />
    public Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return requests.RequestAsync<PrListRequest, PrListReply>(WireTypes.PrList, request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrSummaryReply> SummaryAsync(string prId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        return requests.RequestAsync<PrSummaryRequest, PrSummaryReply>(WireTypes.PrSummary, new PrSummaryRequest(prId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrHunksReply> HunksAsync(PrHunksRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return requests.RequestAsync<PrHunksRequest, PrHunksReply>(WireTypes.PrHunks, request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrReviewReply> ReviewAsync(PrReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Event is not (PrReviewEvents.Comment or PrReviewEvents.RequestChanges))
        {
            throw new ArgumentException("A phone review is a comment or a request for changes. Approval is not a review event.", nameof(request));
        }

        return requests.RequestAsync<PrReviewRequest, PrReviewReply>(WireTypes.PrReview, request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<PrMergeReply> MergeAsync(PrMergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return requests.RequestAsync<PrMergeRequest, PrMergeReply>(WireTypes.PrMerge, request, cancellationToken);
    }
}
