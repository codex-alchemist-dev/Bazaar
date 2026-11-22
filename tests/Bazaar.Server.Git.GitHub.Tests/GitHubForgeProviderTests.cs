using System.Net;
using System.Text.Json.Nodes;
using Bazaar.Server.Git.GitHub.Tests.Support;

namespace Bazaar.Server.Git.GitHub.Tests;

public class GitHubForgeProviderTests
{
    private const string _repo = "/repos/octo/hello";
    private const string _head = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;

    private const string _prJson = """
        {
          "id": 1, "number": 42, "title": "Add thing", "body": "The body", "state": "open", "draft": true,
          "user": { "login": "bazaar-agent[bot]" }, "updated_at": "2026-11-18T10:00:00Z", "merged_at": null,
          "head": { "ref": "feature/x", "sha": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0" },
          "base": { "ref": "main", "sha": "0000000000000000000000000000000000000000" },
          "merged": false, "changed_files": 3, "additions": 40, "deletions": 5
        }
        """;

    private static (GitHubForgeProvider Provider, FakeGitHub Http, FakeCredentialSource Credentials, InstantTimeProvider Time) Create(GitHubOptions? options = null)
    {
        var http = new FakeGitHub();
        var credentials = new FakeCredentialSource();
        var time = new InstantTimeProvider();
        return (new GitHubForgeProvider(new HttpClient(http), credentials, options, time), http, credentials, time);
    }

    private static PrRef Pr(long number = 42) => new("github", "octo/hello", number);

    [Fact]
    public async Task Get_pr_maps_the_detail_and_reviews()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls/42", _prJson);
        http.Json(_get, _repo + "/pulls/42/reviews", """
            [
              { "user": { "login": "alice" }, "state": "APPROVED", "commit_id": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0", "submitted_at": "2026-11-18T11:00:00Z" },
              { "user": { "login": "bob" }, "state": "CHANGES_REQUESTED", "commit_id": "old", "submitted_at": "2026-11-17T11:00:00Z" },
              { "user": { "login": "carol" }, "state": "DISMISSED", "commit_id": "old", "submitted_at": "2026-11-17T12:00:00Z" },
              { "user": { "login": "dave" }, "state": "PENDING", "commit_id": "old" }
            ]
            """);

        var detail = await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal(new PrRef("github", "octo/hello", 42), detail.Summary.Pr);
        Assert.Equal("Add thing", detail.Summary.Title);
        Assert.Equal("bazaar-agent[bot]", detail.Summary.AuthorLogin);
        Assert.Equal(PrState.Open, detail.Summary.State);
        Assert.True(detail.Summary.Draft);
        Assert.Equal(_head, detail.Summary.HeadSha);
        Assert.Equal("feature/x", detail.Summary.Head.Value);
        Assert.Equal("main", detail.Summary.Base.Value);
        Assert.Equal("The body", detail.Body);
        Assert.Equal((3, 40, 5), (detail.ChangedFiles, detail.Additions, detail.Deletions));
        Assert.Collection(
            detail.Reviews,
            r => Assert.Equal(("alice", ReviewEvent.Approve), (r.AuthorLogin, r.Verdict)),
            r => Assert.Equal(("bob", ReviewEvent.RequestChanges), (r.AuthorLogin, r.Verdict)));
    }

    [Fact]
    public async Task Requests_carry_a_bearer_token_and_the_github_headers_and_use_the_read_operation()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_get, _repo + "/pulls/42", _prJson);
        http.Json(_get, _repo + "/pulls/42/reviews", "[]");

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.All(http.Requests, r =>
        {
            Assert.Equal("Bearer " + FakeCredentialSource.Secret, r.Token);
            Assert.Equal("api.github.com", r.Uri.Host);
        });
        Assert.All(credentials.Leases, l =>
        {
            Assert.Equal(GitOperation.Read, l.Operation);
            Assert.Throws<ObjectDisposedException>(() => l.Credential.Reveal());
        });
    }

    [Fact]
    public async Task List_maps_states_and_reports_more_pages_from_the_link_header()
    {
        var (provider, http, _, _) = Create();
        http.Json(
            _get,
            _repo + "/pulls",
            $"[{_prJson}]",
            HttpStatusCode.OK,
            ("Link", "<https://api.github.com/repositories/1/pulls?page=2>; rel=\"next\", <https://api.github.com/repositories/1/pulls?page=3>; rel=\"last\""));

        var page = await provider.ListPrsAsync(new RepoRef("github", "octo/hello"), new ListPrsQuery(PrState.Open, 1, 500), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        var uri = http.Requests.Single().Uri;
        Assert.Contains("state=open", uri.Query);
        Assert.Contains("per_page=100", uri.Query);
        Assert.Contains("page=1", uri.Query);
    }

    [Fact]
    public async Task List_without_a_next_link_has_no_more_pages()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls", $"[{_prJson}]", HttpStatusCode.OK, ("Link", "<https://api.github.com/x?page=1>; rel=\"prev\""));

        var page = await provider.ListPrsAsync(new RepoRef("github", "octo/hello"), new ListPrsQuery(null, 1, 30), CancellationToken.None);

        Assert.False(page.HasMore);
        Assert.Contains("state=all", http.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task List_separates_merged_from_closed()
    {
        var merged = _prJson.Replace("\"state\": \"open\"", "\"state\": \"closed\"").Replace("\"merged_at\": null", "\"merged_at\": \"2026-11-18T12:00:00Z\"").Replace("\"number\": 42", "\"number\": 7");
        var closed = _prJson.Replace("\"state\": \"open\"", "\"state\": \"closed\"").Replace("\"number\": 42", "\"number\": 8");
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls", $"[{merged},{closed}]");
        var repo = new RepoRef("github", "octo/hello");

        var mergedOnly = await provider.ListPrsAsync(repo, new ListPrsQuery(PrState.Merged, 1, 30), CancellationToken.None);
        var closedOnly = await provider.ListPrsAsync(repo, new ListPrsQuery(PrState.Closed, 1, 30), CancellationToken.None);
        var all = await provider.ListPrsAsync(repo, new ListPrsQuery(null, 1, 30), CancellationToken.None);

        Assert.Equal(7, Assert.Single(mergedOnly.Items).Pr.Number);
        Assert.Equal(8, Assert.Single(closedOnly.Items).Pr.Number);
        Assert.Equal(2, all.Items.Count);
        Assert.Contains("state=closed", http.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Files_are_listed_for_the_current_head_with_truncated_patches_marked()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls/42", _prJson);
        http.Json(_get, _repo + "/pulls/42/files", """
            [
              { "filename": "src/a.cs", "status": "modified", "additions": 2, "deletions": 1, "changes": 3, "patch": "@@ -1,2 +1,3 @@\n-old\n+new\n+newer" },
              { "filename": "assets/logo.png", "status": "added", "additions": 0, "deletions": 0, "changes": 0 },
              { "filename": "big.json", "status": "modified", "additions": 5000, "deletions": 10, "changes": 5010 }
            ]
            """);

        var files = await provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None);

        Assert.Collection(
            files,
            f =>
            {
                Assert.Equal(("src/a.cs", "modified", 2, 1), (f.Path, f.Status, f.Additions, f.Deletions));
                Assert.False(f.Truncated);
                Assert.StartsWith("@@ -1,2", f.Patch);
            },
            f => Assert.False(f.Truncated),
            f =>
            {
                Assert.True(f.Truncated);
                Assert.Null(f.Patch);
            });
    }

    [Fact]
    public async Task Files_for_a_stale_head_raise_head_moved_before_any_diff_is_read()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls/42", _prJson.Replace(_head, "ffffffffffffffffffffffffffffffffffffffff"));

        var error = await Assert.ThrowsAsync<GitHubHeadMovedException>(() => provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None));

        Assert.Equal(_head, error.ExpectedHeadSha);
        Assert.Equal("ffffffffffffffffffffffffffffffffffffffff", error.ActualHeadSha);
        Assert.Equal(0, http.Count(_get, _repo + "/pulls/42/files"));
    }

    [Fact]
    public async Task Files_raise_head_moved_when_the_head_changes_during_the_read()
    {
        var (provider, http, _, _) = Create();
        http.Route(
            _get,
            _repo + "/pulls/42",
            FakeGitHub.Build(HttpStatusCode.OK, _prJson),
            FakeGitHub.Build(HttpStatusCode.OK, _prJson.Replace(_head, "ffffffffffffffffffffffffffffffffffffffff")));
        http.Json(_get, _repo + "/pulls/42/files", "[]");

        await Assert.ThrowsAsync<GitHubHeadMovedException>(() => provider.GetPrFilesAsync(Pr(), _head, CancellationToken.None));
    }

    [Fact]
    public async Task Checks_combine_check_runs_and_commit_statuses()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, $"{_repo}/commits/{_head}/check-runs", """
            { "total_count": 6, "check_runs": [
              { "name": "build", "status": "completed", "conclusion": "success", "html_url": "https://github.com/octo/hello/runs/1", "details_url": "https://ci.example.test/1" },
              { "name": "test", "status": "in_progress", "conclusion": null, "html_url": "https://github.com/octo/hello/runs/2" },
              { "name": "lint", "status": "completed", "conclusion": "failure", "html_url": "https://github.com/octo/hello/runs/3" },
              { "name": "docs", "status": "completed", "conclusion": "skipped", "html_url": "https://github.com/octo/hello/runs/4" },
              { "name": "deploy", "status": "completed", "conclusion": "cancelled", "html_url": "https://github.com/octo/hello/runs/5" },
              { "name": "queued", "status": "queued", "conclusion": null, "html_url": "https://github.com/octo/hello/runs/6" }
            ] }
            """);
        http.Json(_get, $"{_repo}/commits/{_head}/status", """
            { "state": "failure", "statuses": [
              { "context": "ci/legacy", "state": "success", "target_url": "https://legacy.example.test/9" },
              { "context": "ci/other", "state": "error", "target_url": null },
              { "context": "ci/wait", "state": "pending", "target_url": null }
            ] }
            """);

        var checks = await provider.GetChecksAsync(Pr(), _head, CancellationToken.None);

        Assert.Equal(
            [
                ("build", CheckState.Success), ("test", CheckState.Pending), ("lint", CheckState.Failure), ("docs", CheckState.Skipped),
                ("deploy", CheckState.Failure), ("queued", CheckState.Pending), ("ci/legacy", CheckState.Success),
                ("ci/other", CheckState.Failure), ("ci/wait", CheckState.Pending),
            ],
            checks.Select(c => (c.Name, c.State)).ToArray());
        Assert.All(checks, c => Assert.Equal(_head, c.HeadSha));
        Assert.Equal("https://ci.example.test/1", checks[0].DetailsUrl);
        Assert.Equal("https://github.com/octo/hello/runs/2", checks[1].DetailsUrl);
    }

    [Fact]
    public async Task Checks_follow_pagination_until_there_is_no_next_link()
    {
        var (provider, http, _, _) = Create();
        http.Route(
            _get,
            $"{_repo}/commits/{_head}/check-runs",
            FakeGitHub.Build(HttpStatusCode.OK, """{"check_runs":[{"name":"one","status":"completed","conclusion":"success"}]}""", ("Link", "<https://api.github.com/x?page=2>; rel=\"next\"")),
            FakeGitHub.Build(HttpStatusCode.OK, """{"check_runs":[{"name":"two","status":"completed","conclusion":"success"}]}"""));
        http.Json(_get, $"{_repo}/commits/{_head}/status", """{"statuses":[]}""");

        var checks = await provider.GetChecksAsync(Pr(), _head, CancellationToken.None);

        Assert.Equal(["one", "two"], checks.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task Open_draft_returns_an_existing_open_pr_for_the_head_without_creating()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls", $"[{_prJson}]");

        var summary = await provider.OpenDraftPrAsync(
            new OpenPrRequest(new RepoRef("github", "octo/hello"), new BranchName("feature/x"), new BranchName("main"), "Add thing", "The body"),
            CancellationToken.None);

        Assert.Equal(42, summary.Pr.Number);
        Assert.Equal(0, http.Count(_post, _repo + "/pulls"));
        Assert.Contains("head=octo%3Afeature%2Fx", http.Requests.Single().Uri.Query);
    }

    [Fact]
    public async Task Open_draft_searches_first_then_creates_a_draft()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_get, _repo + "/pulls", "[]");
        http.Json(_post, _repo + "/pulls", _prJson, HttpStatusCode.Created);

        var summary = await provider.OpenDraftPrAsync(
            new OpenPrRequest(new RepoRef("github", "octo/hello"), new BranchName("feature/x"), new BranchName("main"), "Add thing", "The body"),
            CancellationToken.None);

        Assert.Equal(42, summary.Pr.Number);
        var body = JsonNode.Parse(http.Requests.Single(r => r.Method == _post).Body!)!.AsObject();
        Assert.Equal("Add thing", body["title"]!.GetValue<string>());
        Assert.Equal("feature/x", body["head"]!.GetValue<string>());
        Assert.Equal("main", body["base"]!.GetValue<string>());
        Assert.True(body["draft"]!.GetValue<bool>());
        Assert.Equal([GitOperation.Read, GitOperation.Push], credentials.Leases.Select(l => l.Operation).ToArray());
    }

    [Fact]
    public async Task Post_review_pins_the_event_to_the_sha()
    {
        var (provider, http, credentials, _) = Create();
        http.Json(_post, _repo + "/pulls/42/reviews", "{\"id\":1}");

        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Approve, "LGTM"), CancellationToken.None);
        await provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.RequestChanges, "Fix"), CancellationToken.None);
        await provider.PostReviewAsync(Pr(), new ReviewRequest(string.Empty, ReviewEvent.Comment, "Note"), CancellationToken.None);

        var bodies = http.Requests.Select(r => JsonNode.Parse(r.Body!)!.AsObject()).ToList();
        Assert.Equal(("APPROVE", _head), (bodies[0]["event"]!.GetValue<string>(), bodies[0]["commit_id"]!.GetValue<string>()));
        Assert.Equal("REQUEST_CHANGES", bodies[1]["event"]!.GetValue<string>());
        Assert.Equal("COMMENT", bodies[2]["event"]!.GetValue<string>());
        Assert.Null(bodies[2]["commit_id"]);
        Assert.All(credentials.Leases, l => Assert.Equal(GitOperation.Push, l.Operation));
    }

    [Fact]
    public async Task Approve_without_a_sha_is_refused_before_any_call()
    {
        var (provider, http, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.PostReviewAsync(Pr(), new ReviewRequest(string.Empty, ReviewEvent.Approve, "LGTM"), CancellationToken.None));

        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Client_errors_are_typed_not_retried_and_do_not_leak_the_token_or_body(HttpStatusCode status)
    {
        var (provider, http, _, time) = Create();
        http.Json(_get, _repo + "/pulls/42", "{\"message\":\"secret-body-text\"}", status);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Null(error.RetryAfter);
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.ToString());
        Assert.DoesNotContain("secret-body-text", error.ToString());
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task A_429_waits_for_retry_after_then_succeeds()
    {
        var (provider, http, _, time) = Create();
        http.Route(
            _get,
            _repo + "/pulls/42",
            FakeGitHub.Build(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "12")),
            FakeGitHub.Build(HttpStatusCode.OK, _prJson));
        http.Json(_get, _repo + "/pulls/42/reviews", "[]");

        var detail = await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal(42, detail.Summary.Pr.Number);
        Assert.Equal([TimeSpan.FromSeconds(12)], time.Delays);
    }

    [Fact]
    public async Task A_retry_after_longer_than_the_cap_is_cut_to_the_cap()
    {
        var (provider, http, _, time) = Create(new GitHubOptions { MaxRetries = 1, MaxRetryAfter = TimeSpan.FromSeconds(5) });
        http.Json(_get, _repo + "/pulls/42", "{}", HttpStatusCode.TooManyRequests, ("Retry-After", "600"));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), error.RetryAfter);
        Assert.Equal([TimeSpan.FromSeconds(5)], time.Delays);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task A_secondary_rate_limit_403_with_retry_after_is_retried()
    {
        var (provider, http, _, time) = Create();
        http.Route(
            _get,
            _repo + "/pulls/42",
            FakeGitHub.Build(HttpStatusCode.Forbidden, "{\"message\":\"secondary rate limit\"}", ("Retry-After", "30")),
            FakeGitHub.Build(HttpStatusCode.OK, _prJson));
        http.Json(_get, _repo + "/pulls/42/reviews", "[]");

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(30)], time.Delays);
    }

    [Fact]
    public async Task An_exhausted_primary_quota_waits_until_reset_when_it_is_near()
    {
        var (provider, http, _, time) = Create();
        var reset = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Route(
            _get,
            _repo + "/pulls/42",
            FakeGitHub.Build(HttpStatusCode.Forbidden, "{}", ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset)),
            FakeGitHub.Build(HttpStatusCode.OK, _prJson));
        http.Json(_get, _repo + "/pulls/42/reviews", "[]");

        await provider.GetPrAsync(Pr(), CancellationToken.None);

        var delay = Assert.Single(time.Delays);
        Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(21));
    }

    [Fact]
    public async Task An_exhausted_primary_quota_with_a_far_reset_fails_at_once_with_the_wait()
    {
        var (provider, http, _, time) = Create();
        var reset = DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Json(_get, _repo + "/pulls/42", "{}", HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.NotNull(error.RetryAfter);
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task A_403_with_quota_left_is_a_plain_permission_error()
    {
        var (provider, http, _, time) = Create();
        http.Json(_get, _repo + "/pulls/42", "{}", HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "4999"));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Null(error.RetryAfter);
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task A_5xx_on_get_backs_off_and_retries_then_gives_up_with_a_typed_error()
    {
        var (provider, http, _, time) = Create(new GitHubOptions { MaxRetries = 2 });
        http.Json(_get, _repo + "/pulls/42", "{}", HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetPrAsync(Pr(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], time.Delays);
    }

    [Fact]
    public async Task A_5xx_on_post_is_not_replayed()
    {
        var (provider, http, _, _) = Create();
        http.Json(_post, _repo + "/pulls/42/reviews", "{}", HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<GitHubApiException>(() => provider.PostReviewAsync(Pr(), new ReviewRequest(_head, ReviewEvent.Approve, "ok"), CancellationToken.None));

        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_non_json_body_is_a_typed_error_that_does_not_echo_the_text()
    {
        var (provider, http, _, _) = Create();
        http.Json(_get, _repo + "/pulls/42", "<html>secret-html</html>");

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => provider.GetHeadShaAsync(Pr(), CancellationToken.None));

        Assert.Null(error.StatusCode);
        Assert.DoesNotContain("secret-html", error.ToString());
    }

    [Fact]
    public async Task Enterprise_base_urls_with_a_path_are_honoured()
    {
        var (provider, http, _, _) = Create(new GitHubOptions { BaseUrl = new Uri("https://ghe.example.test/api/v3") });
        http.Json(_get, "/api/v3/repos/octo/hello/pulls/42", _prJson);

        var sha = await provider.GetHeadShaAsync(Pr(), CancellationToken.None);

        Assert.Equal(_head, sha);
        Assert.Equal("ghe.example.test", http.Requests.Single().Uri.Host);
    }

    [Theory]
    [InlineData("http://api.example.test")]
    [InlineData("api/v3")]
    public void A_non_https_base_url_is_refused(string baseUrl)
    {
        var options = new GitHubOptions { BaseUrl = new Uri(baseUrl, UriKind.RelativeOrAbsolute) };
        Assert.Throws<ArgumentException>(() => new GitHubForgeProvider(new HttpClient(), new FakeCredentialSource(), options));
    }

    [Theory]
    [InlineData("octo")]
    [InlineData("group/sub/project")]
    public async Task A_full_name_that_is_not_owner_slash_name_is_refused(string fullName)
    {
        var (provider, http, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetPrAsync(new PrRef("github", fullName, 1), CancellationToken.None));

        Assert.Empty(http.Requests);
    }

    [Fact]
    public void The_provider_reports_github()
    {
        var (provider, _, _, _) = Create();
        Assert.Equal("github", provider.ProviderId);
    }
}
