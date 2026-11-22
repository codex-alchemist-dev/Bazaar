using System.Net;
using System.Text.Json.Nodes;
using Bazaar.Server.Git.GitLab.Tests.Support;

namespace Bazaar.Server.Git.GitLab.Tests;

public class GitLabForgeProviderTests
{
    private const string _project = "/api/v4/projects/group%2Fsub%2Fproject";
    private const string _head = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;

    private const string _mrJson = """
        {
          "id": 9001, "iid": 42, "title": "Draft: Add thing", "description": "The body",
          "state": "opened", "draft": true, "sha": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0",
          "source_branch": "feature/x", "target_branch": "main",
          "author": { "username": "bazaar-agent" }, "updated_at": "2026-11-18T10:00:00.000Z",
          "changes_count": "1000+", "web_url": "https://gitlab.com/group/sub/project/-/merge_requests/42"
        }
        """;

    private static (GitLabForgeProvider Provider, FakeGitLab Http, FakeCredentialSource Credentials, InstantTimeProvider Time) Create(
        GitLabOptions? options = null)
    {
        var http = new FakeGitLab();
        var credentials = new FakeCredentialSource();
        var time = new InstantTimeProvider();
        var provider = new GitLabForgeProvider(new HttpClient(http), credentials, options, time);
        return (provider, http, credentials, time);
    }

    private static PrRef Pr(long number = 42) => new("gitlab", "group/sub/project", number);

    [Fact]
    public async Task Open_draft_searches_first_and_creates_once_across_a_retry()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests", "[]")
            .Json(_post, _project + "/merge_requests", _mrJson, HttpStatusCode.Created);
        var request = new OpenPrRequest(new RepoRef("gitlab", "group/sub/project"), new BranchName("feature/x"), new BranchName("main"), "Add thing", "The body");

        var first = await provider.OpenDraftPrAsync(request, CancellationToken.None);

        Assert.Equal("Draft: Add thing", first.Title);
        Assert.Equal(42, first.Pr.Number);
        Assert.Equal(1, http.Count(_post, _project + "/merge_requests"));

        // The caller retries: the search now finds the open merge request, so no second create happens.
        http.Json(_get, _project + "/merge_requests", $"[{_mrJson}]");
        var retry = await provider.OpenDraftPrAsync(request, CancellationToken.None);

        Assert.Equal(first.Pr, retry.Pr);
        Assert.Equal(1, http.Count(_post, _project + "/merge_requests"));
    }

    [Fact]
    public async Task Open_draft_sends_branch_filter_and_a_draft_title()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests", "[]")
            .Json(_post, _project + "/merge_requests", _mrJson, HttpStatusCode.Created);

        await provider.OpenDraftPrAsync(new OpenPrRequest(new RepoRef("gitlab", "group/sub/project"), new BranchName("feature/x"), new BranchName("main"), "Add thing", "The body"), CancellationToken.None);

        var search = http.Requests.Single(r => r.Method == _get);
        Assert.Contains("source_branch=feature%2Fx", search.Uri.Query);
        var create = JsonNode.Parse(http.Requests.Single(r => r.Method == _post).Body!)!.AsObject();
        Assert.Equal("Draft: Add thing", create["title"]!.GetValue<string>());
        Assert.Equal("feature/x", create["source_branch"]!.GetValue<string>());
        Assert.Equal("main", create["target_branch"]!.GetValue<string>());
    }

    [Fact]
    public async Task Open_draft_does_not_double_the_draft_prefix()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests", "[]")
            .Json(_post, _project + "/merge_requests", _mrJson, HttpStatusCode.Created);

        await provider.OpenDraftPrAsync(new OpenPrRequest(new RepoRef("gitlab", "group/sub/project"), new BranchName("feature/x"), new BranchName("main"), "Draft: Add thing", "b"), CancellationToken.None);

        var create = JsonNode.Parse(http.Requests.Single(r => r.Method == _post).Body!)!.AsObject();
        Assert.Equal("Draft: Add thing", create["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_maps_states_and_pages_from_x_next_page()
    {
        var (provider, http, _, _) = Create();
        var page1 = $"[{_mrJson}, {_mrJson.Replace("\"iid\": 42", "\"iid\": 43").Replace("\"state\": \"opened\"", "\"state\": \"merged\"")}]";
        http.Route(_get, _project + "/merge_requests",
            FakeGitLab.Build(HttpStatusCode.OK, page1, ("X-Next-Page", "2")),
            FakeGitLab.Build(HttpStatusCode.OK, "[]", ("X-Next-Page", "")));
        var repo = new RepoRef("gitlab", "group/sub/project");

        var first = await provider.ListPrsAsync(repo, new ListPrsQuery(null, 1, 2), CancellationToken.None);
        var second = await provider.ListPrsAsync(repo, new ListPrsQuery(PrState.Open, 2, 2), CancellationToken.None);

        Assert.True(first.HasMore);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(PrState.Open, first.Items[0].State);
        Assert.Equal(PrState.Merged, first.Items[1].State);
        Assert.Equal(43, first.Items[1].Pr.Number);
        Assert.False(second.HasMore);
        Assert.Empty(second.Items);

        var requests = http.Requests.Where(r => r.Method == _get).ToList();
        Assert.Contains("state=all", requests[0].Uri.Query);
        Assert.Contains("per_page=2", requests[0].Uri.Query);
        Assert.Contains("state=opened", requests[1].Uri.Query);
        Assert.Contains("page=2", requests[1].Uri.Query);
    }

    [Fact]
    public async Task Get_pr_reads_changes_count_with_a_plus_suffix()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);

        var detail = await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal(1000, detail.ChangedFiles);
        Assert.Equal("The body", detail.Body);
        Assert.Equal("bazaar-agent", detail.Summary.AuthorLogin);
        Assert.Equal(_head, detail.Summary.HeadSha);
        Assert.True(detail.Summary.Draft);
    }

    [Fact]
    public async Task Get_pr_treats_a_missing_changes_count_as_zero()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson.Replace("\"changes_count\": \"1000+\"", "\"changes_count\": null"));

        var detail = await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal(0, detail.ChangedFiles);
    }

    [Fact]
    public async Task Files_map_status_counts_and_collapsed_diffs()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);
        const string diffs = """
            [
              { "old_path": "src/new.cs", "new_path": "src/new.cs", "new_file": true, "deleted_file": false, "renamed_file": false,
                "collapsed": false, "too_large": false, "diff": "@@ -0,0 +1,2 @@\n+line one\n+line two\n" },
              { "old_path": "src/gone.cs", "new_path": "src/gone.cs", "new_file": false, "deleted_file": true, "renamed_file": false,
                "collapsed": false, "too_large": false, "diff": "@@ -1 +0,0 @@\n-old\n" },
              { "old_path": "a.txt", "new_path": "b.txt", "new_file": false, "deleted_file": false, "renamed_file": true,
                "collapsed": false, "too_large": false, "diff": "@@ -1 +1 @@\n-x\n+y\n" },
              { "old_path": "c.cs", "new_path": "c.cs", "new_file": false, "deleted_file": false, "renamed_file": false,
                "collapsed": false, "too_large": false, "diff": "@@ -1 +1 @@\n-a\n+b\n" },
              { "old_path": "big.bin", "new_path": "big.bin", "new_file": false, "deleted_file": false, "renamed_file": false,
                "collapsed": true, "too_large": false, "diff": "" }
            ]
            """;
        http.Json(_get, _project + "/merge_requests/42/diffs", diffs);

        var files = await provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None);

        Assert.Equal(5, files.Count);
        Assert.Equal(new PrFile("src/new.cs", "added", 2, 0, "@@ -0,0 +1,2 @@\n+line one\n+line two\n", false), files[0]);
        Assert.Equal(new PrFile("src/gone.cs", "removed", 0, 1, "@@ -1 +0,0 @@\n-old\n", false), files[1]);
        Assert.Equal("renamed", files[2].Status);
        Assert.Equal("b.txt", files[2].Path);
        Assert.Equal((1, 1), (files[2].Additions, files[2].Deletions));
        Assert.Equal("modified", files[3].Status);
        Assert.True(files[4].Truncated);
        Assert.Null(files[4].Patch);
    }

    [Fact]
    public async Task Files_follow_x_next_page_for_diffs()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);
        const string one = """[{ "old_path": "a", "new_path": "a", "diff": "+x\n" }]""";
        http.Route(_get, _project + "/merge_requests/42/diffs",
            FakeGitLab.Build(HttpStatusCode.OK, one, ("X-Next-Page", "2")),
            FakeGitLab.Build(HttpStatusCode.OK, """[{ "old_path": "b", "new_path": "b", "diff": "+y\n" }]""", ("X-Next-Page", "")));

        var files = await provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None);

        Assert.Equal(["a", "b"], files.Select(f => f.Path));
    }

    [Fact]
    public async Task Files_refuse_when_the_head_moved_and_do_not_read_the_diff()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson.Replace(_head, "ffffffffffffffffffffffffffffffffffffffff"));

        var error = await Assert.ThrowsAsync<GitLabHeadMovedException>(() => provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None));

        Assert.Equal(42, error.MergeRequestIid);
        Assert.Equal(_head, error.ExpectedHeadSha);
        Assert.Equal("ffffffffffffffffffffffffffffffffffffffff", error.ActualHeadSha);
        Assert.Equal(0, http.Count(_get, _project + "/merge_requests/42/diffs"));
    }

    [Fact]
    public async Task Checks_map_pipeline_job_states_for_the_head_pipeline()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42/pipelines",
            $"[{{\"id\": 500, \"sha\": \"0000000000000000000000000000000000000000\"}}, {{\"id\": 77, \"sha\": \"{_head}\"}}]");
        const string jobs = """
            [
              { "name": "build", "status": "success", "web_url": "https://gitlab.com/j/1" },
              { "name": "test", "status": "failed", "web_url": "https://gitlab.com/j/2" },
              { "name": "lint", "status": "running", "web_url": null },
              { "name": "deploy", "status": "manual", "web_url": null },
              { "name": "docs", "status": "canceled", "web_url": null },
              { "name": "x", "status": "skipped", "web_url": null },
              { "name": "y", "status": "created", "web_url": null },
              { "name": "z", "status": "pending", "web_url": null }
            ]
            """;
        http.Json(_get, _project + "/pipelines/77/jobs", jobs);

        var checks = await provider.GetChecksAsync(Pr(), _head, CancellationToken.None);

        Assert.Equal(
            [CheckState.Success, CheckState.Failure, CheckState.Pending, CheckState.Skipped, CheckState.Skipped, CheckState.Skipped, CheckState.Pending, CheckState.Pending],
            checks.Select(c => c.State));
        Assert.All(checks, c => Assert.Equal(_head, c.HeadSha));
        Assert.Equal("https://gitlab.com/j/2", checks[1].DetailsUrl);
        Assert.Null(checks[2].DetailsUrl);
    }

    [Fact]
    public async Task Checks_are_empty_when_no_pipeline_matches_the_head()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42/pipelines", "[{\"id\": 500, \"sha\": \"0000000000000000000000000000000000000000\"}]");

        var checks = await provider.GetChecksAsync(Pr(), _head, CancellationToken.None);

        Assert.Empty(checks);
        Assert.Equal(0, http.Count(_get, _project + "/pipelines/500/jobs"));
    }

    [Fact]
    public async Task Head_sha_is_the_merge_request_sha()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);

        Assert.Equal(_head, await provider.GetHeadShaAsync(Pr(), CancellationToken.None));
    }

    [Fact]
    public async Task Comment_posts_a_note_with_the_body()
    {
        var (provider, http, _, _) = Create();
        http.Json(_post, _project + "/merge_requests/42/notes", "{}", HttpStatusCode.Created);

        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Comment, "Looks fine"), CancellationToken.None);

        var note = JsonNode.Parse(http.Requests.Single().Body!)!.AsObject();
        Assert.Equal("Looks fine", note["body"]!.GetValue<string>());
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Approve_posts_an_approval_pinned_to_the_head_sha()
    {
        var (provider, http, _, _) = Create();
        http.Json(_post, _project + "/merge_requests/42/approve", "{}", HttpStatusCode.Created);

        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Approve, "ok"), CancellationToken.None);

        var approval = JsonNode.Parse(http.Requests.Single().Body!)!.AsObject();
        Assert.Equal(_head, approval["sha"]!.GetValue<string>());
        Assert.Equal(1, http.Count(_post, _project + "/merge_requests/42/approve"));
        Assert.Equal(0, http.Count(_post, _project + "/merge_requests/42/notes"));
    }

    [Fact]
    public async Task Request_changes_is_a_note_that_leads_with_the_verdict()
    {
        var (provider, http, _, _) = Create();
        http.Json(_post, _project + "/merge_requests/42/notes", "{}", HttpStatusCode.Created);

        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.RequestChanges, "Rename it"), CancellationToken.None);

        var note = JsonNode.Parse(http.Requests.Single().Body!)!.AsObject();
        Assert.Equal("Changes requested:\n\nRename it", note["body"]!.GetValue<string>());
        Assert.Equal(0, http.Count(_post, _project + "/merge_requests/42/approve"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Auth_and_missing_errors_are_not_retried(HttpStatusCode status)
    {
        var (provider, http, _, time) = Create();
        http.Json(_get, _project + "/merge_requests/42", "{\"message\":\"nope\"}", status);

        var error = await Assert.ThrowsAsync<GitLabApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Rate_limit_waits_for_retry_after_then_succeeds()
    {
        var (provider, http, _, time) = Create();
        http.Route(_get, _project + "/merge_requests/42",
            FakeGitLab.Build(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "7")),
            FakeGitLab.Build(HttpStatusCode.OK, _mrJson));

        var detail = await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal(42, detail.Summary.Pr.Number);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], time.Delays);
    }

    [Fact]
    public async Task Retry_after_is_capped_by_max_retry_after()
    {
        var (provider, http, _, time) = Create(new GitLabOptions { MaxRetryAfter = TimeSpan.FromSeconds(30) });
        http.Route(_get, _project + "/merge_requests/42",
            FakeGitLab.Build(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "3600")),
            FakeGitLab.Build(HttpStatusCode.OK, _mrJson));

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(30)], time.Delays);
    }

    [Fact]
    public async Task Service_unavailable_is_retried_until_the_budget_is_spent()
    {
        var (provider, http, _, time) = Create(new GitLabOptions { MaxRetries = 3 });
        http.Json(_get, _project + "/merge_requests/42", "{}", HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<GitLabApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(4, http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], time.Delays);
    }

    [Fact]
    public async Task A_post_is_not_replayed_on_a_server_error()
    {
        var (provider, http, _, time) = Create();
        http.Json(_post, _project + "/merge_requests/42/notes", "{}", HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<GitLabApiException>(() => provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Comment, "x"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task The_token_is_sent_per_request_and_never_appears_in_an_error()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", "{\"message\":\"forbidden\"}", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<GitLabApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(FakeCredentialSource.Secret, http.Requests.Single().Token);
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.Message);
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.ToString());
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.InnerException?.Message ?? string.Empty);
        Assert.Single(credentials.Leases);
    }

    [Fact]
    public async Task Every_lease_is_disposed_after_its_request()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        var lease = Assert.Single(credentials.Leases);
        Assert.Equal(GitOperation.Read, lease.Operation);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
    }

    [Fact]
    public async Task Writes_lease_a_push_credential_and_reads_lease_a_read_credential()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);
        http.Json(_post, _project + "/merge_requests/42/notes", "{}", HttpStatusCode.Created);

        await provider.GetPrAsync(Pr(), CancellationToken.None);
        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Comment, "x"), CancellationToken.None);

        Assert.Equal([GitOperation.Read, GitOperation.Push], credentials.Leases.Select(l => l.Operation));
        Assert.All(credentials.Leases, l => Assert.Equal("gitlab", l.Repo.Provider));
    }

    [Fact]
    public async Task Subgroup_paths_are_one_url_encoded_segment()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _project + "/merge_requests/42", _mrJson);

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("/api/v4/projects/group%2Fsub%2Fproject/merge_requests/42", request.Path);
        Assert.Equal("https://gitlab.com/api/v4/projects/group%2Fsub%2Fproject/merge_requests/42", request.Uri.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task A_self_managed_base_url_is_used_with_its_own_api_root()
    {
        var (provider, http, _, _) = Create(new GitLabOptions { BaseUrl = new Uri("https://git.example.test/gitlab/") });
        http.Route(_get, "/gitlab/api/v4/projects/group%2Fsub%2Fproject/merge_requests/42", FakeGitLab.Build(HttpStatusCode.OK, _mrJson));

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal("/gitlab/api/v4/projects/group%2Fsub%2Fproject/merge_requests/42", http.Requests.Single().Path);
    }

    [Fact]
    public void A_non_https_base_url_is_refused()
    {
        var http = new HttpClient(new FakeGitLab());
        Assert.Throws<ArgumentException>(() => new GitLabForgeProvider(http, new FakeCredentialSource(), new GitLabOptions { BaseUrl = new Uri("http://gitlab.test/") }));
    }

    [Fact]
    public async Task A_response_that_is_not_json_is_a_typed_error()
    {
        var (provider, http, _, _) = Create();
        http.Route(_get, _project + "/merge_requests/42", FakeGitLab.Build(HttpStatusCode.OK, "<html>not json"));

        var error = await Assert.ThrowsAsync<GitLabApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Null(error.StatusCode);
        Assert.DoesNotContain("<html>", error.Message);
    }

    [Fact]
    public void Provider_id_is_gitlab()
    {
        var (provider, _, _, _) = Create();
        Assert.Equal("gitlab", provider.ProviderId);
    }
}
