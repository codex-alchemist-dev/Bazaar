using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The phone's view of the server-proxied PR wire (doc 21, Client PR wire). Clients never call GitHub and never see a token.
/// Every call goes through the authenticated connection; a refusal or a lost connection surfaces as an exception.
/// There is deliberately no approve: reviewer approval is data, never merge authority.
/// </summary>
public interface IPullRequestClient
{
    /// <summary>One page of PRs.</summary>
    Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken);

    /// <summary>The PR at its current head.</summary>
    Task<PrSummaryReply> SummaryAsync(string prId, CancellationToken cancellationToken);

    /// <summary>Hunks of one file, keyed by the head sha the caller is looking at.</summary>
    Task<PrHunksReply> HunksAsync(PrHunksRequest request, CancellationToken cancellationToken);

    /// <summary>A comment or request-changes review against one head sha. Any other event is refused before sending.</summary>
    Task<PrReviewReply> ReviewAsync(PrReviewRequest request, CancellationToken cancellationToken);

    /// <summary>The owner-signed squash merge. Only the owner's device builds the request.</summary>
    Task<PrMergeReply> MergeAsync(PrMergeRequest request, CancellationToken cancellationToken);
}
