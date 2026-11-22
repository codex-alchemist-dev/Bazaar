using Bazaar.Contracts.GitHub;
using Bazaar.GitHub.Adapter;

namespace Bazaar.GitHub.Wire;

/// <summary>
/// The server-side PR read model behind <c>pr.list</c>, <c>pr.summary</c> and the merge checks gate (doc 21, Client PR wire).
/// It owns what <see cref="IGitHubAdapter"/> does not expose: the PR directory (<c>github_pr</c>), summaries, checks and the
/// card link. Its implementation is not part of this change; the wire handlers are tested against fakes.
/// </summary>
public interface IGitHubPrReadModel
{
    /// <summary>Resolves a wire PR id (the PR node id) to the installation, repo and number, or null when unknown.</summary>
    Task<PrRef?> ResolveAsync(string prId, CancellationToken cancellationToken);

    /// <summary>One page of PRs for the filter.</summary>
    Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken);

    /// <summary>The PR summary at its current head, or null when the PR is unknown.</summary>
    Task<PrSummaryReply?> GetSummaryAsync(string prId, PrRef pr, CancellationToken cancellationToken);

    /// <summary>The checks rollup at <paramref name="headSha"/>.</summary>
    Task<PrChecksRollup> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken);
}
