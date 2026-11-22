using System.Net;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// The GitHub squash merge (doc 21 merge path, doc 52). It is registered beside <see cref="GitHubForgeProvider"/> but is reachable
/// only through the owner-signed merge gate in <c>Bazaar.Server.Git</c>. The merge is pinned to one head sha, so a moved head
/// makes GitHub refuse it (409) instead of merging unreviewed code. The credential is whatever the credential source leases for a
/// Push on this repo (doc 21: the owner-class merge credential, chosen by <c>github.merge_credential</c>).
/// </summary>
public sealed class GitHubMergeExecutor : IForgeMergeExecutor
{
    private readonly GitHubApiClient _api;

    public GitHubMergeExecutor(HttpClient http, IGitCredentialSource credentials, GitHubOptions? options = null, TimeProvider? time = null)
    {
        _api = new GitHubApiClient(http, credentials, options ?? new GitHubOptions(), time ?? TimeProvider.System);
    }

    public string ProviderId => GitHubApiClient.ProviderIdValue;

    /// <summary>
    /// Squash-merges with <c>sha</c> set to the approved head. The commit title is the request title and the commit message is the
    /// owner's <c>Signed-off-by</c> line. A 405, 409 or 422 refusal, or a 200 that is not <c>merged: true</c>, becomes
    /// <see cref="GitHubMergeRefusedException"/>.
    /// </summary>
    public async Task SquashMergeAsync(PrRef pr, MergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.HeadSha);

        var body = new JsonObject
        {
            ["sha"] = request.HeadSha,
            ["merge_method"] = "squash",
            ["commit_title"] = request.CommitTitle,
            ["commit_message"] = request.SignOff ?? string.Empty,
        };

        try
        {
            var response = await _api.SendAsync(
                GitOperation.Push,
                pr.FullName,
                HttpMethod.Put,
                GitHubApiClient.PullPath(pr, "/merge"),
                null,
                body,
                cancellationToken);
            using var doc = GitHubApiClient.Parse(response.Body, "pull request merge");
            if (GitHubMapping.OptionalBool(doc.RootElement, "merged") != true)
            {
                throw new GitHubMergeRefusedException(null, "GitHub did not report the pull request as merged.");
            }
        }
        catch (GitHubApiException ex) when (ex.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            throw new GitHubMergeRefusedException(ex.StatusCode, ex.Message, ex);
        }
    }
}
