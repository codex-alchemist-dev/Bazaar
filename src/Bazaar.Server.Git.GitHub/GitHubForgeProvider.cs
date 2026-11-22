using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// The GitHub forge adapter (doc 52 work package 7, doc 21 provider scope), over the REST API. A pull request is keyed by its
/// number and its head sha. Reads take a Read credential and writes take a Push credential. Nothing here merges: the squash merge
/// is on <see cref="GitHubMergeExecutor"/>, which only the merge gate may call.
/// </summary>
public sealed class GitHubForgeProvider : IForgeProvider
{
    private readonly GitHubApiClient _api;

    public GitHubForgeProvider(HttpClient http, IGitCredentialSource credentials, GitHubOptions? options = null, TimeProvider? time = null)
    {
        _api = new GitHubApiClient(http, credentials, options ?? new GitHubOptions(), time ?? TimeProvider.System);
    }

    public string ProviderId => GitHubApiClient.ProviderIdValue;

    public async Task<PrSummary> OpenDraftPrAsync(OpenPrRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fullName = request.Repo.FullName;
        var path = GitHubApiClient.RepoPath(fullName, "pulls");
        var owner = fullName.Split('/')[0];

        // Search first so a retried open call never creates a second pull request for the same head branch.
        var search = await _api.SendAsync(
            GitOperation.Read,
            fullName,
            HttpMethod.Get,
            path,
            [("state", "open"), ("head", $"{owner}:{request.Head.Value}")],
            null,
            cancellationToken);
        using (var found = GitHubApiClient.Parse(search.Body, "pull request search"))
        {
            var items = GitHubMapping.RequireArray(found.RootElement, "pull request search");
            if (items.GetArrayLength() > 0)
            {
                return GitHubMapping.ToPrSummary(items[0], fullName);
            }
        }

        var body = new JsonObject
        {
            ["title"] = request.Title,
            ["body"] = request.Body,
            ["head"] = request.Head.Value,
            ["base"] = request.Base.Value,
            ["draft"] = true,
        };
        var created = await _api.SendAsync(GitOperation.Push, fullName, HttpMethod.Post, path, null, body, cancellationToken);
        using var doc = GitHubApiClient.Parse(created.Body, "pull request create");
        return GitHubMapping.ToPrSummary(doc.RootElement, fullName);
    }

    /// <summary>
    /// GitHub has no merged filter on the list call, so Merged and Closed ask for closed pull requests and keep the matching ones.
    /// A page can therefore hold fewer items than the page size while <c>HasMore</c> is still true.
    /// </summary>
    public async Task<PagedResult<PrSummary>> ListPrsAsync(RepoRef repo, ListPrsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(query);
        var state = query.State switch
        {
            null => "all",
            PrState.Open => "open",
            PrState.Merged or PrState.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(query), "The PR state is not recognised."),
        };
        var page = Math.Max(1, query.Page);

        var response = await _api.SendAsync(
            GitOperation.Read,
            repo.FullName,
            HttpMethod.Get,
            GitHubApiClient.RepoPath(repo.FullName, "pulls"),
            [("state", state), ("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitHubApiClient.PerPage(query.PageSize))],
            null,
            cancellationToken);
        using var doc = GitHubApiClient.Parse(response.Body, "pull request list");
        var items = new List<PrSummary>();
        foreach (var pr in GitHubMapping.RequireArray(doc.RootElement, "pull request list").EnumerateArray())
        {
            var summary = GitHubMapping.ToPrSummary(pr, repo.FullName);
            if (query.State is null || summary.State == query.State)
            {
                items.Add(summary);
            }
        }

        return new PagedResult<PrSummary>(items, response.HasNextPage);
    }

    public async Task<PrDetail> GetPrAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var response = await _api.SendAsync(GitOperation.Read, pr.FullName, HttpMethod.Get, GitHubApiClient.PullPath(pr), null, null, cancellationToken);
        var reviews = await ReadReviewsAsync(pr, cancellationToken);
        using var doc = GitHubApiClient.Parse(response.Body, "pull request");
        return GitHubMapping.ToPrDetail(doc.RootElement, pr.FullName, reviews);
    }

    /// <summary>
    /// The changed files at <paramref name="headSha"/>. The head is checked before and after the listing, so a force-push during
    /// the read raises <see cref="GitHubHeadMovedException"/> instead of returning hunks for a different commit.
    /// </summary>
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
                GitHubApiClient.PullPath(pr, "/files"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitHubApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitHubApiClient.Parse(response.Body, "pull request files");
            foreach (var file in GitHubMapping.RequireArray(doc.RootElement, "pull request files").EnumerateArray())
            {
                files.Add(GitHubMapping.ToPrFile(file));
            }

            if (!response.HasNextPage)
            {
                await RequireHeadAsync(pr, headSha, cancellationToken);
                return files;
            }
        }
    }

    /// <summary>The check runs and the commit statuses of <paramref name="headSha"/>, both read from the commit itself.</summary>
    public async Task<IReadOnlyList<CheckRun>> GetChecksAsync(PrRef pr, string headSha, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentException.ThrowIfNullOrEmpty(headSha);
        var sha = Uri.EscapeDataString(headSha);

        var checks = new List<CheckRun>();
        for (var page = 1; ; page++)
        {
            var runs = await _api.SendAsync(
                GitOperation.Read,
                pr.FullName,
                HttpMethod.Get,
                GitHubApiClient.RepoPath(pr.FullName, $"commits/{sha}/check-runs"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitHubApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitHubApiClient.Parse(runs.Body, "check runs");
            foreach (var run in GitHubMapping.RequireArrayProperty(doc.RootElement, "check_runs", "check runs").EnumerateArray())
            {
                checks.Add(new CheckRun(
                    GitHubMapping.RequiredString(run, "name"),
                    GitHubMapping.ToCheckState(GitHubMapping.OptionalString(run, "status"), GitHubMapping.OptionalString(run, "conclusion")),
                    headSha,
                    GitHubMapping.OptionalString(run, "details_url") ?? GitHubMapping.OptionalString(run, "html_url")));
            }

            if (!runs.HasNextPage)
            {
                break;
            }
        }

        for (var page = 1; ; page++)
        {
            var statuses = await _api.SendAsync(
                GitOperation.Read,
                pr.FullName,
                HttpMethod.Get,
                GitHubApiClient.RepoPath(pr.FullName, $"commits/{sha}/status"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitHubApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitHubApiClient.Parse(statuses.Body, "commit status");
            foreach (var status in GitHubMapping.RequireArrayProperty(doc.RootElement, "statuses", "commit status").EnumerateArray())
            {
                checks.Add(new CheckRun(
                    GitHubMapping.RequiredString(status, "context"),
                    GitHubMapping.ToStatusState(GitHubMapping.OptionalString(status, "state")),
                    headSha,
                    GitHubMapping.OptionalString(status, "target_url")));
            }

            if (!statuses.HasNextPage)
            {
                return checks;
            }
        }
    }

    public async Task<string> GetHeadShaAsync(PrRef pr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var response = await _api.SendAsync(GitOperation.Read, pr.FullName, HttpMethod.Get, GitHubApiClient.PullPath(pr), null, null, cancellationToken);
        using var doc = GitHubApiClient.Parse(response.Body, "pull request");
        return GitHubMapping.NestedString(doc.RootElement, "head", "sha") ?? throw new GitHubApiException(null, "GitHub response is missing 'head.sha'.");
    }

    /// <summary>
    /// Posts a review. Approve and RequestChanges are pinned to the sha with <c>commit_id</c>. Comment is pinned too when a sha
    /// is given.
    /// </summary>
    public async Task PostReviewAsync(PrRef pr, ReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request);
        var reviewEvent = request.Event switch
        {
            ReviewEvent.Comment => "COMMENT",
            ReviewEvent.Approve => "APPROVE",
            ReviewEvent.RequestChanges => "REQUEST_CHANGES",
            _ => throw new ArgumentException("A review event must be Comment, Approve or RequestChanges.", nameof(request)),
        };
        if (request.Event != ReviewEvent.Comment)
        {
            ArgumentException.ThrowIfNullOrEmpty(request.HeadSha);
        }

        var body = new JsonObject { ["body"] = request.Body, ["event"] = reviewEvent };
        if (!string.IsNullOrEmpty(request.HeadSha))
        {
            body["commit_id"] = request.HeadSha;
        }

        await _api.SendAsync(GitOperation.Push, pr.FullName, HttpMethod.Post, GitHubApiClient.PullPath(pr, "/reviews"), null, body, cancellationToken);
    }

    private async Task<IReadOnlyList<ReviewSummary>> ReadReviewsAsync(PrRef pr, CancellationToken cancellationToken)
    {
        var reviews = new List<ReviewSummary>();
        for (var page = 1; ; page++)
        {
            var response = await _api.SendAsync(
                GitOperation.Read,
                pr.FullName,
                HttpMethod.Get,
                GitHubApiClient.PullPath(pr, "/reviews"),
                [("page", page.ToString(CultureInfo.InvariantCulture)), ("per_page", GitHubApiClient.PerPage(100))],
                null,
                cancellationToken);
            using var doc = GitHubApiClient.Parse(response.Body, "pull request reviews");
            foreach (var item in GitHubMapping.RequireArray(doc.RootElement, "pull request reviews").EnumerateArray())
            {
                if (GitHubMapping.ToReview(item) is { } review)
                {
                    reviews.Add(review);
                }
            }

            if (!response.HasNextPage)
            {
                return reviews;
            }
        }
    }

    private async Task RequireHeadAsync(PrRef pr, string expectedHeadSha, CancellationToken cancellationToken)
    {
        var actual = await GetHeadShaAsync(pr, cancellationToken);
        if (!string.Equals(actual, expectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubHeadMovedException(pr.Number, expectedHeadSha, actual);
        }
    }
}
