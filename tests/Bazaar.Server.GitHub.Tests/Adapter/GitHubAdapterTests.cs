using System.Net;
using System.Text.Json;
using Bazaar.Server.GitHub.Adapter;
using Bazaar.Server.GitHub.Auth;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.GitHub.Tests;

public sealed class GitHubAdapterTests
{
    private const long Installation = 77;
    private const string Owner = "codex-alchemist-dev";
    private const string Repo = "bazaar";
    private const string Token = "ghs_TESTONLY_do_not_leak_0123456789";
    private const string HeadSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string OldSha = "0000000000000000000000000000000000000001";

    private const string PullsPath = "/repos/codex-alchemist-dev/bazaar/pulls";
    private const string SearchPath = "/repos/codex-alchemist-dev/bazaar/pulls?head=codex-alchemist-dev%3Aagent%2Fx&state=open";
    private const string FilesPath = "/repos/codex-alchemist-dev/bazaar/pulls/42/files?per_page=100";

    private static readonly OpenPrRequest OpenRequest = new(Installation, Owner, Repo, "agent/x", "main", "Add x", "Body text");
    private static readonly PrRef Pr = new(Installation, Owner, Repo, 42);

    private const string FilesJson =
        """[{"filename": "src/a.cs", "status": "modified", "additions": 3, "deletions": 1, "patch": "@@ -1 +1 @@"}]""";

    private static string PullJson(int number, string sha) =>
        "{\"number\": " + number + ", \"node_id\": \"PR_kwDO" + number + "\", \"draft\": true, \"state\": \"open\", " +
        "\"head\": {\"sha\": \"" + sha + "\", \"ref\": \"agent/x\"}}";

    private sealed record Harness(
        GitHubAdapter Adapter,
        ScriptedHttpHandler Http,
        RecordingTimeProvider Time,
        StubTokenProvider Tokens,
        InMemoryGitHubRateState Rates,
        CapturingLogger<GitHubAdapter> Logs);

    private static Harness Build(RecordingTimeProvider? time = null)
    {
        var http = new ScriptedHttpHandler();
        var clock = time ?? new RecordingTimeProvider();
        var tokens = new StubTokenProvider(Token);
        var rates = new InMemoryGitHubRateState();
        var logs = new CapturingLogger<GitHubAdapter>();
        var options = new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") };
        var adapter = new GitHubAdapter(new HttpClient(http), tokens, rates, options, logs, clock);
        return new Harness(adapter, http, clock, tokens, rates, logs);
    }

    [Fact]
    public async Task Open_creates_a_draft_pr_when_no_open_pr_has_the_head()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, SearchPath, Reply.Json(HttpStatusCode.OK, "[]"));
        h.Http.Route(HttpMethod.Post, PullsPath, Reply.Json(HttpStatusCode.Created, PullJson(42, HeadSha)));

        var pr = await h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None);

        Assert.Equal(42, pr.Pr.Number);
        Assert.Equal(Installation, pr.Pr.InstallationId);
        Assert.Equal("PR_kwDO42", pr.NodeId);
        Assert.Equal(HeadSha, pr.HeadSha);
        Assert.True(pr.Draft);

        var post = Assert.Single(h.Http.Requests, r => r.Method == "POST");
        Assert.Equal($"Bearer {Token}", post.Authorization);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean());
        Assert.Equal("agent/x", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("Add x", body.RootElement.GetProperty("title").GetString());

        Assert.Equal(Installation, Assert.Single(h.Tokens.Scopes).InstallationId);
    }

    [Fact]
    public async Task Open_returns_the_existing_pr_without_posting()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, SearchPath, Reply.Json(HttpStatusCode.OK, $"[{PullJson(9, HeadSha)}]"));

        var pr = await h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None);

        Assert.Equal(9, pr.Pr.Number);
        Assert.Single(h.Http.Requests);
        Assert.DoesNotContain(h.Http.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Conditional_read_sends_etag_and_returns_cached_body_on_304()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, PullsPath + "/42", Reply.Json(HttpStatusCode.OK, PullJson(42, HeadSha), ("ETag", "\"pr-1\"")));
        h.Http.Route(HttpMethod.Get, FilesPath, Reply.Json(HttpStatusCode.OK, FilesJson, ("ETag", "\"files-1\"")));
        h.Http.Route(HttpMethod.Get, PullsPath + "/42", Reply.NotModified("\"pr-1\""));
        h.Http.Route(HttpMethod.Get, FilesPath, Reply.NotModified("\"files-1\""));

        var first = await h.Adapter.GetPrFilesAsync(Pr, HeadSha, CancellationToken.None);
        var second = await h.Adapter.GetPrFilesAsync(Pr, HeadSha, CancellationToken.None);

        Assert.Equal("src/a.cs", Assert.Single(first).Path);
        var cachedFile = Assert.Single(second);
        Assert.Equal("src/a.cs", cachedFile.Path);
        Assert.Equal("@@ -1 +1 @@", cachedFile.Patch);

        var conditional = h.Http.Requests.Where(r => r.IfNoneMatch is not null).ToList();
        Assert.Equal(2, conditional.Count);
        Assert.Equal("\"pr-1\"", conditional[0].IfNoneMatch);
        Assert.Equal("\"files-1\"", conditional[1].IfNoneMatch);
    }

    [Fact]
    public async Task Forbidden_with_retry_after_waits_then_succeeds()
    {
        var h = Build();
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(HttpStatusCode.Forbidden, """{"message": "You have exceeded a secondary rate limit."}""", ("Retry-After", "2")),
            Reply.Json(HttpStatusCode.OK, "[]"));
        h.Http.Route(HttpMethod.Post, PullsPath, Reply.Json(HttpStatusCode.Created, PullJson(7, HeadSha)));

        var pr = await h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None);

        Assert.Equal(7, pr.Pr.Number);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(h.Time.Delays));
        Assert.Equal(3, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Rate_limited_429_past_the_inline_wait_raises_with_reset_then_defers_reads_locally()
    {
        var h = Build();
        var reset = h.Time.Now.AddHours(1);
        var resetUnix = reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(
                HttpStatusCode.TooManyRequests,
                """{"message": "API rate limit exceeded"}""",
                ("x-ratelimit-limit", "5000"),
                ("x-ratelimit-remaining", "0"),
                ("x-ratelimit-reset", resetUnix)));

        var first = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset.ToUnixTimeSeconds()), first.ResetAt);
        Assert.Equal(Installation, first.InstallationId);
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Empty(h.Time.Delays);
        Assert.Single(h.Http.Requests);

        // The window is now at 100%, so the next read is deferred without touching GitHub.
        var second = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None));
        Assert.Equal(first.ResetAt, second.ResetAt);
        Assert.Single(h.Http.Requests);
    }

    [Fact]
    public async Task Server_errors_back_off_exponentially_then_succeed()
    {
        var h = Build();
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            Reply.Json(HttpStatusCode.BadGateway, "{}"),
            Reply.Json(HttpStatusCode.OK, "[]"));
        h.Http.Route(HttpMethod.Post, PullsPath, Reply.Json(HttpStatusCode.Created, PullJson(5, HeadSha)));

        var pr = await h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None);

        Assert.Equal(5, pr.Pr.Number);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Time.Delays);
    }

    [Fact]
    public async Task Server_errors_give_up_after_three_retries_with_the_status()
    {
        var h = Build();
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(HttpStatusCode.InternalServerError, """{"message": "boom"}"""),
            Reply.Json(HttpStatusCode.InternalServerError, """{"message": "boom"}"""),
            Reply.Json(HttpStatusCode.InternalServerError, """{"message": "boom"}"""),
            Reply.Json(HttpStatusCode.InternalServerError, """{"message": "boom"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("boom", ex.GitHubMessage);
        Assert.Equal(4, h.Http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], h.Time.Delays);
    }

    [Fact]
    public async Task Stale_head_sha_is_rejected_before_the_files_are_read()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, PullsPath + "/42", Reply.Json(HttpStatusCode.OK, PullJson(42, OldSha)));

        var ex = await Assert.ThrowsAsync<GitHubStaleHeadException>(() => h.Adapter.GetPrFilesAsync(Pr, HeadSha, CancellationToken.None));

        Assert.Equal(42, ex.PullNumber);
        Assert.Equal(HeadSha, ex.ExpectedHeadSha);
        Assert.Equal(OldSha, ex.ActualHeadSha);
        Assert.Single(h.Http.Requests);
    }

    [Fact]
    public async Task Token_never_appears_in_exception_text_even_when_github_echoes_it()
    {
        var h = Build();
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(HttpStatusCode.UnprocessableEntity, $$"""{"message": "Validation Failed: bad credential {{Token}}"}"""));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.GitHubMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Squash_merge_sends_head_sha_and_the_message_as_given()
    {
        var h = Build();
        h.Http.Route(
            HttpMethod.Put,
            PullsPath + "/42/merge",
            Reply.Json(HttpStatusCode.OK, $$"""{"merged": true, "sha": "{{HeadSha}}", "message": "Merged"}"""));
        const string message = "Body of the change\n\nSigned-off-by: Owner <owner@example.test>";

        await h.Adapter.SquashMergeAsync(Pr, new MergeRequest(HeadSha, "Add x (#42)", message), CancellationToken.None);

        var put = Assert.Single(h.Http.Requests);
        using var body = JsonDocument.Parse(put.Body!);
        Assert.Equal("squash", body.RootElement.GetProperty("merge_method").GetString());
        Assert.Equal(HeadSha, body.RootElement.GetProperty("sha").GetString());
        Assert.Equal("Add x (#42)", body.RootElement.GetProperty("commit_title").GetString());
        Assert.Equal(message, body.RootElement.GetProperty("commit_message").GetString());
        Assert.Equal("write", Assert.Single(h.Tokens.Scopes).Permissions["contents"]);
    }

    [Fact]
    public async Task Squash_merge_fails_when_github_does_not_confirm_the_merge()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Put, PullsPath + "/42/merge", Reply.Json(HttpStatusCode.OK, """{"merged": false}"""));

        await Assert.ThrowsAsync<GitHubApiException>(() =>
            h.Adapter.SquashMergeAsync(Pr, new MergeRequest(HeadSha, "t", "m"), CancellationToken.None));
    }

    [Fact]
    public async Task Review_pins_the_head_sha_and_rejects_unknown_events()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Post, PullsPath + "/42/reviews", Reply.Json(HttpStatusCode.OK, """{"id": 1}"""));

        await h.Adapter.PostReviewAsync(Pr, new ReviewRequest(HeadSha, "COMMENT", "Looks fine"), CancellationToken.None);

        var post = Assert.Single(h.Http.Requests);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(HeadSha, body.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", body.RootElement.GetProperty("event").GetString());
        Assert.Equal("Looks fine", body.RootElement.GetProperty("body").GetString());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Adapter.PostReviewAsync(Pr, new ReviewRequest(HeadSha, "PENDING", "x"), CancellationToken.None));
        Assert.Single(h.Http.Requests);
    }

    [Fact]
    public async Task Files_follow_pagination_links_on_the_configured_host()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, PullsPath + "/42", Reply.Json(HttpStatusCode.OK, PullJson(42, HeadSha)));
        h.Http.Route(
            HttpMethod.Get,
            FilesPath,
            Reply.Json(HttpStatusCode.OK, FilesJson, ("Link", "<https://api.test/repos/codex-alchemist-dev/bazaar/pulls/42/files?per_page=100&page=2>; rel=\"next\"")));
        h.Http.Route(
            HttpMethod.Get,
            "/repos/codex-alchemist-dev/bazaar/pulls/42/files?per_page=100&page=2",
            Reply.Json(HttpStatusCode.OK, FilesJson));

        var files = await h.Adapter.GetPrFilesAsync(Pr, HeadSha, CancellationToken.None);

        Assert.Equal(2, files.Count);
        Assert.Equal(3, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Pagination_link_off_the_configured_host_is_refused_and_never_fetched()
    {
        var h = Build();
        h.Http.Route(HttpMethod.Get, PullsPath + "/42", Reply.Json(HttpStatusCode.OK, PullJson(42, HeadSha)));
        h.Http.Route(
            HttpMethod.Get,
            FilesPath,
            Reply.Json(HttpStatusCode.OK, FilesJson, ("Link", "<https://evil.example/files>; rel=\"next\"")));

        await Assert.ThrowsAsync<GitHubApiException>(() => h.Adapter.GetPrFilesAsync(Pr, HeadSha, CancellationToken.None));

        Assert.All(h.Http.Requests, r => Assert.StartsWith("https://api.test/", r.Url, StringComparison.Ordinal));
        Assert.Equal(2, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Usage_at_70_percent_logs_a_warning_and_the_call_still_succeeds()
    {
        var h = Build();
        var reset = h.Time.Now.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        h.Http.Route(
            HttpMethod.Get,
            SearchPath,
            Reply.Json(
                HttpStatusCode.OK,
                "[]",
                ("x-ratelimit-limit", "5000"),
                ("x-ratelimit-remaining", "1400"),
                ("x-ratelimit-reset", reset)));
        h.Http.Route(HttpMethod.Post, PullsPath, Reply.Json(HttpStatusCode.Created, PullJson(3, HeadSha)));

        var pr = await h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None);

        Assert.Equal(3, pr.Pr.Number);
        Assert.Contains(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("72%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_defer_locally_at_90_percent_without_calling_github()
    {
        var h = Build();
        await h.Rates.SetAsync(Installation, new GitHubRateSnapshot(5000, 400, h.Time.Now.AddMinutes(30)), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<GitHubRateLimitedException>(() => h.Adapter.OpenDraftPrAsync(OpenRequest, CancellationToken.None));

        Assert.Equal(h.Time.Now.AddMinutes(30), ex.ResetAt);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public void Non_https_base_address_is_rejected_because_the_token_travels_in_it()
    {
        var options = new GitHubAdapterOptions { BaseAddress = new Uri("http://api.test/") };

        Assert.Throws<ArgumentException>(() => new GitHubAdapter(
            new HttpClient(new ScriptedHttpHandler()),
            new StubTokenProvider(Token),
            new InMemoryGitHubRateState(),
            options,
            new CapturingLogger<GitHubAdapter>()));
    }
}
