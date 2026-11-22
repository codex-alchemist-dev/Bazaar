namespace Bazaar.Server.Community.GitHub;

/// <summary>A created or found GitHub item. <see cref="ExternalId"/> is GitHub's numeric id as text.</summary>
public sealed record CommunityGitHubItem(string ExternalId, Uri Url);

/// <summary>
/// The GitHub REST calls for issues and comments (Bazaar.GitHub has no issue or comment writes). Pull requests go through
/// <see cref="ICommunityPullRequestOpener"/>. Failures surface as <see cref="CommunityPostException"/> with the right
/// <see cref="CommunityPostException.Retryable"/> flag.
/// </summary>
public interface ICommunityGitHubApi
{
    Task<CommunityGitHubItem> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken cancellationToken);

    Task<CommunityGitHubItem> CreateCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken);

    /// <summary>
    /// Finds an item whose body carries <paramref name="marker"/>. With <paramref name="issueNumber"/> null it searches the repo's
    /// issues and pull requests; otherwise it scans that issue's comments.
    /// </summary>
    Task<CommunityGitHubItem?> FindByMarkerAsync(string owner, string repo, int? issueNumber, string marker, CancellationToken cancellationToken);
}
