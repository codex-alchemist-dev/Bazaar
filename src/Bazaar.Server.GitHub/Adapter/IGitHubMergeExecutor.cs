namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// The squash-merge call itself. Internal on purpose: a merge is reachable only through <see cref="OwnerSignedMerge"/>, which
/// checks the owner's signed approval first (doc 21, Merge request path; C25).
/// </summary>
internal interface IGitHubMergeExecutor
{
    Task SquashMergeAsync(PrRef pr, MergeRequest merge, CancellationToken cancellationToken);
}
