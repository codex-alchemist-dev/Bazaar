using System.Net;
using System.Text.Json.Nodes;
using Bazaar.Server.Git.GitHub.Tests.Support;

namespace Bazaar.Server.Git.GitHub.Tests;

public class GitHubMergeExecutorTests
{
    private const string _merge = "/repos/octo/hello/pulls/42/merge";
    private const string _head = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    private static (GitHubMergeExecutor Executor, FakeGitHub Http, FakeCredentialSource Credentials, InstantTimeProvider Time) Create()
    {
        var http = new FakeGitHub();
        var credentials = new FakeCredentialSource();
        var time = new InstantTimeProvider();
        return (new GitHubMergeExecutor(new HttpClient(http), credentials, time: time), http, credentials, time);
    }

    private static MergeRequest Request(string signOff = "Signed-off-by: The Owner <owner@example.test>") =>
        new(_head, "Add thing (#42)", signOff);

    private static PrRef Pr() => new("github", "octo/hello", 42);

    [Fact]
    public async Task Squash_merge_is_pinned_to_the_head_with_the_sign_off_in_the_message()
    {
        var (executor, http, _, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"sha\":\"deadbeef\",\"merged\":true,\"message\":\"Pull Request successfully merged\"}");

        await executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None);

        var sent = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, sent.Method);
        var expected = JsonNode.Parse("""
            {
              "sha": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0",
              "merge_method": "squash",
              "commit_title": "Add thing (#42)",
              "commit_message": "Signed-off-by: The Owner <owner@example.test>"
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(sent.Body!)));
    }

    [Fact]
    public async Task Merge_uses_a_push_credential_sent_as_a_bearer_token_and_disposes_it()
    {
        var (executor, http, credentials, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"merged\":true}");

        await executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None);

        var lease = Assert.Single(credentials.Leases);
        Assert.Equal(GitOperation.Push, lease.Operation);
        Assert.Equal(new RepoRef("github", "octo/hello"), lease.Repo);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
        Assert.Equal("Bearer " + FakeCredentialSource.Secret, http.Requests.Single().Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_refused_merge_is_a_typed_refusal_and_is_not_retried(HttpStatusCode status)
    {
        var (executor, http, _, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"message\":\"Head branch was modified. Review and try the merge again.\"}", status);

        var error = await Assert.ThrowsAsync<GitHubMergeRefusedException>(() => executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_200_that_does_not_report_merged_is_a_refusal()
    {
        var (executor, http, _, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"merged\":false,\"message\":\"not merged\"}");

        await Assert.ThrowsAsync<GitHubMergeRefusedException>(() => executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Auth_and_missing_failures_are_not_refusals_and_do_not_leak_the_token(HttpStatusCode status)
    {
        var (executor, http, _, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"message\":\"nope\"}", status);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None));

        Assert.IsNotType<GitHubMergeRefusedException>(error);
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.ToString());
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_server_error_on_merge_is_never_replayed()
    {
        var (executor, http, _, time) = Create();
        http.Json(HttpMethod.Put, _merge, "{}", HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Single(http.Requests);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task A_429_on_merge_waits_for_retry_after_and_retries_once()
    {
        var (executor, http, _, time) = Create();
        http.Route(
            HttpMethod.Put,
            _merge,
            FakeGitHub.Build(HttpStatusCode.TooManyRequests, "{}", ("Retry-After", "7")),
            FakeGitHub.Build(HttpStatusCode.OK, "{\"merged\":true}"));

        await executor.SquashMergeAsync(Pr(), Request(), CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], time.Delays);
    }

    [Fact]
    public void The_executor_reports_github()
    {
        var (executor, _, _, _) = Create();
        Assert.Equal("github", executor.ProviderId);
    }

    [Fact]
    public void An_https_only_base_url_is_required()
    {
        var http = new FakeGitHub();
        var options = new GitHubOptions { BaseUrl = new Uri("http://ghe.example.test/api/v3") };
        Assert.Throws<ArgumentException>(() => new GitHubMergeExecutor(new HttpClient(http), new FakeCredentialSource(), options));
    }
}
