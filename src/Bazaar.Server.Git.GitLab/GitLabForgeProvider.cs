using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// The GitLab forge adapter (doc 52, doc 21 provider scope), over REST v4. A merge request is keyed by its <c>iid</c> and its
/// <c>sha</c> (the head). Reads take a Read credential and writes take a Push credential. Nothing here merges: the squash merge
/// is on <see cref="GitLabMergeExecutor"/>, which only the merge gate may call.
/// </summary>
public sealed class GitLabForgeProvider : IForgeProvider
{
    private const string _draftPrefix = "Draft: ";

    private readonly GitLabApiClient _api;

    public GitLabForgeProvider(HttpClient http, IGitCredentialSource credentials, GitLabOptions? options = null, TimeProvider? time = null)
    {
        _api = new GitLabApiClient(http, credentials, options ?? new GitLabOptions(), time ?? TimeProvider.System);
    }

    public string ProviderId => GitLabApiClient.ProviderIdValue;

    public async Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fullName = request.Repo.FullName;
        var path = GitLabApiClient.ProjectPath(fullName, "merge_requests");

        // Search first so a retried open call never creates a second merge request for the same source branch.
        var search = await _api.SendAsync(
            GitOperation.Read,
            fullName,
            HttpMethod.Get,
            path,
            [("state", "opened"), ("source_branch", request.Head.Value)],
            null,
            cancellationToken);
        using (var found = GitLabApiClient.Parse(search.Body, "merge request search"))
        {
            var items = GitLabMapping.RequireArray(found, "merge request search");
            if (items.GetArrayLength() > 0)
            {
                return GitLabMapping.ToPrSummary(items[0], fullName);
            }
        }

        var body = new JsonObject
        {
            ["source_branch"] = request.Head.Value,
            ["target_branch"] = request.Base.Value,
            ["title"] = DraftTitle(request.Title),
            ["description"] = request.Body,
        };
        var created = await _api.SendAsync(GitOperation.Push, fullName, HttpMethod.Post, path, null, body, cancellationToken);
        using var doc = GitLabApiClient.Parse(created.Body, "merge request create");
        return GitLabMapping.ToPrSummary(doc.RootElement, fullName);
    }

    public async Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(query);
        var state = query.State switch
        {
            null => "all",
            PrState.Open => "opened",
            PrState.Merged => "merged",
            PrState.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(query), "The PR state is not recognised."),
        };
        var page = Math.Max(1, query.Page);

        var response = await _api.SendAsync(
            GitOperation.Read,
            repo.FullName,
            HttpMethod.Get,
            GitLabApiClient.ProjectPath(repo.FullName, "merge_requests"),
            [("state", state), ("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitLabApiClient.PerPage(query.PageSize))],
            null,
            cancellationToken);
        using var doc = GitLabApiClient.Parse(response.Body, "merge request list");
        var items = new List<PrSummary>();
        foreach (var mr in GitLabMapping.RequireArray(doc, "merge request list").EnumerateArray())
        {
            items.Add(GitLabMapping.ToPrSummary(mr, repo.FullName));
        }

        return new PagedResult<PrSummary>(items, response.HasNextPage);
    }

    public async Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var response = await _api.SendAsync(GitOperation.Read, pr.FullName, HttpMethod.Get, GitLabApiClient.MergeRequestPath(pr), null, null, cancellationToken);
        using var doc = GitLabApiClient.Parse(response.Body, "merge request");
        return GitLabMapping.ToPrDetail(doc.RootElement, pr.FullName);
    }

    public async Task<IReadOnlyList<PrFile>> GetPrFilesAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrEmpty(headSha);
        await RequireHeadAsync(pr, headSha, cancellationToken);

        var files = new List<PrFile>();
        for (var page = 1; ; page++)
        {
            var response = await _api.SendAsync(
                GitOperation.Read,
                pr.FullName,
                HttpMethod.Get,
                GitLabApiClient.MergeRequestPath(pr, "/diffs"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitLabApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitLabApiClient.Parse(response.Body, "merge request diffs");
            foreach (var diff in GitLabMapping.RequireArray(doc, "merge request diffs").EnumerateArray())
            {
                files.Add(GitLabMapping.ToPrFile(diff));
            }

            if (!response.HasNextPage)
            {
                return files;
            }
        }
    }

    /// <summary>
    /// The head pipeline's jobs. The pipeline is the first one on this merge request whose <c>sha</c> is the head. With no
    /// pipeline for that head, there are no checks yet.
    /// </summary>
    public async Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrEmpty(headSha);

        var pipelines = await _api.SendAsync(
            GitOperation.Read,
            pr.FullName,
            HttpMethod.Get,
            GitLabApiClient.MergeRequestPath(pr, "/pipelines"),
            [("per_page", GitLabApiClient.PerPage(100))],
            null,
            cancellationToken);
        long? pipelineId = null;
        using (var doc = GitLabApiClient.Parse(pipelines.Body, "merge request pipelines"))
        {
            foreach (var pipeline in GitLabMapping.RequireArray(doc, "merge request pipelines").EnumerateArray())
            {
                if (string.Equals(GitLabMapping.OptionalString(pipeline, "sha"), headSha, StringComparison.OrdinalIgnoreCase))
                {
                    pipelineId = GitLabMapping.RequiredLong(pipeline, "id");
                    break;
                }
            }
        }

        if (pipelineId is not { } id)
        {
            return [];
        }

        var checks = new List<CheckRun>();
        for (var page = 1; ; page++)
        {
            var jobs = await _api.SendAsync(
                GitOperation.Read,
                pr.FullName,
                HttpMethod.Get,
                GitLabApiClient.ProjectPath(pr.FullName, $"pipelines/{id.ToString(CultureInfo.InvariantCulture)}/jobs"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitLabApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitLabApiClient.Parse(jobs.Body, "pipeline jobs");
            foreach (var job in GitLabMapping.RequireArray(doc, "pipeline jobs").EnumerateArray())
            {
                var state = GitLabMapping.ToCheckState(GitLabMapping.RequiredString(job, "status"));
                checks.Add(new CheckRun(
                    GitLabMapping.RequiredString(job, "name"),
                    state,
                    headSha,
                    GitLabMapping.OptionalString(job, "web_url")));
            }

            if (!jobs.HasNextPage)
            {
                return checks;
            }
        }
    }

    public async Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var response = await _api.SendAsync(GitOperation.Read, pr.FullName, HttpMethod.Get, GitLabApiClient.MergeRequestPath(pr), null, null, cancellationToken);
        using var doc = GitLabApiClient.Parse(response.Body, "merge request");
        return GitLabMapping.RequiredString(doc.RootElement, "sha");
    }

    /// <summary>
    /// Approve posts GitLab's approval pinned to the sha. Comment posts a note. GitLab has no request-changes review state, so
    /// RequestChanges posts a note that leads with <c>Changes requested:</c>.
    /// </summary>
    public async Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Event)
        {
            case ReviewEvent.Comment:
                await PostNoteAsync(pr, request.Body, cancellationToken);
                break;
            case ReviewEvent.Approve:
                ArgumentException.ThrowIfNullOrEmpty(request.HeadSha);
                var approval = new JsonObject { ["sha"] = request.HeadSha };
                await _api.SendAsync(GitOperation.Push, pr.FullName, HttpMethod.Post, GitLabApiClient.MergeRequestPath(pr, "/approve"), null, approval, cancellationToken);
                break;
            case ReviewEvent.RequestChanges:
                await PostNoteAsync(pr, "Changes requested:\n\n" + request.Body, cancellationToken);
                break;
            default:
                throw new ArgumentException("A review event must be Comment, Approve or RequestChanges.", nameof(request));
        }
    }

    private async Task PostNoteAsync(PrRef pr, string text, CancellationToken cancellationToken)
    {
        var note = new JsonObject { ["body"] = text };
        await _api.SendAsync(GitOperation.Push, pr.FullName, HttpMethod.Post, GitLabApiClient.MergeRequestPath(pr, "/notes"), null, note, cancellationToken);
    }

    private async Task RequireHeadAsync(PrRef pr, string expectedHeadSha, CancellationToken cancellationToken)
    {
        var actual = await GetHeadShaAsync(pr, cancellationToken);
        if (!string.Equals(actual, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitLabHeadMovedException(pr.Number, expectedHeadSha, actual);
        }
    }

    private static string DraftTitle(string title) =>
        title.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) ? title : _draftPrefix + title;
}
