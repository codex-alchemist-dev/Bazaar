using System.Net;

namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// A GitHub call failed. <see cref="StatusCode"/> is null when no HTTP response arrived or the body was unusable.
/// Messages never contain the installation token.
/// </summary>
public class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode? statusCode, string? gitHubMessage, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        GitHubMessage = gitHubMessage;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The <c>message</c> field of GitHub's error body, with any token value removed.</summary>
    public string? GitHubMessage { get; }
}

/// <summary>
/// GitHub refused the call for rate reasons, or reads are deferred locally at the defer threshold. <see cref="ResetAt"/>
/// is when the caller may try again.
/// </summary>
public sealed class GitHubRateLimitedException : GitHubApiException
{
    public GitHubRateLimitedException(long installationId, DateTimeOffset resetAt, HttpStatusCode? statusCode, string message)
        : base(statusCode, null, message)
    {
        InstallationId = installationId;
        ResetAt = resetAt;
    }

    public long InstallationId { get; }

    public DateTimeOffset ResetAt { get; }
}

/// <summary>A PR's head moved since the caller last saw it, so its file list no longer matches the approval or review.</summary>
public sealed class GitHubStaleHeadException : Exception
{
    public GitHubStaleHeadException(int pullNumber, string expectedHeadSha, string actualHeadSha)
        : base($"Pull request #{pullNumber} head is {actualHeadSha}, not the expected {expectedHeadSha}.")
    {
        PullNumber = pullNumber;
        ExpectedHeadSha = expectedHeadSha;
        ActualHeadSha = actualHeadSha;
    }

    public int PullNumber { get; }

    public string ExpectedHeadSha { get; }

    public string ActualHeadSha { get; }
}
