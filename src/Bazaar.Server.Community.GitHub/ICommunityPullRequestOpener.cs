namespace Bazaar.Server.Community.GitHub;

/// <summary>One draft pull request to open. <paramref name="Body"/> already carries the hidden draft marker.</summary>
public sealed record CommunityPullRequestRequest(
    Guid DraftId, string Owner, string Repo, string Title, string Body, string HeadBranch, string BaseBranch);

/// <summary>
/// Opens a draft pull request. The host binds this to Bazaar.Server.GitHub's <c>IGitHubAdapter.OpenDraftPrAsync</c> (which searches by head
/// branch before creating, so it is idempotent and returns the existing PR as <c>Reused</c>). This module does not reimplement PR
/// opening and does not reference Bazaar.Server.GitHub. Failures are <see cref="CommunityPostException"/>; the opener sets the retryable flag.
/// </summary>
public interface ICommunityPullRequestOpener
{
    Task<PostReceipt> OpenDraftAsync(CommunityPullRequestRequest request, CancellationToken cancellationToken);
}
