using System.Net;

namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// A GitHub call failed. <see cref="StatusCode"/> is null when no HTTP status was available or the body was not usable.
/// <see cref="RetryAfter"/> is set when GitHub rate-limited the call (429, or a 403 for a primary or secondary limit).
/// Messages never contain the token or the response body.
/// </summary>
public class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode? statusCode, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>How long GitHub asked callers to wait, when the failure was a rate limit.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// GitHub refused the squash merge: the pull request is not mergeable (405), its head no longer matches the pinned sha (409), or
/// the request was invalid (422). The merge is void and the owner re-approves.
/// </summary>
public sealed class GitHubMergeRefusedException(HttpStatusCode? statusCode, string message, Exception? inner = null)
    : GitHubApiException(statusCode, message, inner);

/// <summary>A pull request's head moved since the caller last saw it, so its diff no longer matches the approval or review.</summary>
public sealed class GitHubHeadMovedException : Exception
{
    public GitHubHeadMovedException(long pullRequestNumber, string expectedHeadSha, string actualHeadSha)
        : base($"Pull request #{pullRequestNumber} head is {actualHeadSha}, not the expected {expectedHeadSha}.")
    {
        PullRequestNumber = pullRequestNumber;
        ExpectedHeadSha = expectedHeadSha;
        ActualHeadSha = actualHeadSha;
    }

    public long PullRequestNumber { get; }

    public string ExpectedHeadSha { get; }

    public string ActualHeadSha { get; }
}
