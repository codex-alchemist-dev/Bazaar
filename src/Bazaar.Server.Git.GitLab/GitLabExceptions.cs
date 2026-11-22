using System.Net;

namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// A GitLab call failed. <see cref="StatusCode"/> is null when no HTTP status was available or the body was not usable.
/// Messages never contain the token or the response body.
/// </summary>
public class GitLabApiException : Exception
{
    public GitLabApiException(HttpStatusCode? statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// GitLab refused the squash merge: the merge request is not mergeable (405, 406) or its head no longer matches the pinned sha (409).
/// The merge is void and the owner re-approves.
/// </summary>
public sealed class GitLabMergeRefusedException(HttpStatusCode? statusCode, string message, Exception? inner = null)
    : GitLabApiException(statusCode, message, inner);

/// <summary>A merge request's head moved since the caller last saw it, so its diff no longer matches the approval or review.</summary>
public sealed class GitLabHeadMovedException : Exception
{
    public GitLabHeadMovedException(long mergeRequestIid, string expectedHeadSha, string actualHeadSha)
        : base($"Merge request !{mergeRequestIid} head is {actualHeadSha}, not the expected {expectedHeadSha}.")
    {
        MergeRequestIid = mergeRequestIid;
        ExpectedHeadSha = expectedHeadSha;
        ActualHeadSha = actualHeadSha;
    }

    public long MergeRequestIid { get; }

    public string ExpectedHeadSha { get; }

    public string ActualHeadSha { get; }
}
