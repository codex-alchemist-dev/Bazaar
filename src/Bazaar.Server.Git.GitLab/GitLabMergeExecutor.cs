using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// The GitLab squash merge (doc 21 merge path, doc 52). It is registered beside <see cref="GitLabForgeProvider"/> but is reachable
/// only through the owner-signed merge gate in <c>Bazaar.Server.Git</c>. The merge is pinned to one head sha, so a moved head
/// makes GitLab refuse it (409) instead of merging unreviewed code.
/// </summary>
public sealed class GitLabMergeExecutor : IForgeMergeExecutor
{
    private readonly GitLabApiClient _api;

    public GitLabMergeExecutor(HttpClient http, IGitCredentialSource credentials, GitLabOptions? options = null, TimeProvider? time = null)
    {
        _api = new GitLabApiClient(http, credentials, options ?? new GitLabOptions(), time ?? TimeProvider.System);
    }

    public string ProviderId => GitLabApiClient.ProviderIdValue;

    /// <summary>
    /// Squash-merges with <c>sha</c> set to the approved head. The commit message is the title, a blank line, then the owner's
    /// <c>Signed-off-by</c> line. The source branch is kept. A 405, 406 or 409 refusal becomes <see cref="GitLabMergeRefusedException"/>.
    /// </summary>
    public async Task SquashMergeAsync(PrRef pr, MergeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.HeadSha);

        var message = string.IsNullOrEmpty(request.SignOff) ? request.CommitTitle : $"{request.CommitTitle}\n\n{request.SignOff}";
        var body = new JsonObject
        {
            ["sha"] = request.HeadSha,
            ["squash"] = true,
            ["squash_commit_message"] = message,
            ["should_remove_source_branch"] = false,
        };

        try
        {
            var response = await _api.SendAsync(
                GitOperation.Push,
                pr.FullName,
                HttpMethod.Put,
                GitLabApiClient.MergeRequestPath(pr, "/merge"),
                null,
                body,
                cancellationToken);
            using var doc = GitLabApiClient.Parse(response.Body, "merge request merge");
            if (GitLabMapping.OptionalString(doc.RootElement, "state") != "merged")
            {
                throw new GitLabMergeRefusedException(null, "GitLab did not report the merge request as merged.");
            }
        }
        catch (GitLabApiException ex) when (ex.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotAcceptable or HttpStatusCode.Conflict)
        {
            throw new GitLabMergeRefusedException(ex.StatusCode, ex.Message, ex);
        }
    }
}
