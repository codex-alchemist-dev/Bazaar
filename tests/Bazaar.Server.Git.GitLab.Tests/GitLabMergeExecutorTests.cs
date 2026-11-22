using System.Net;
using System.Text.Json.Nodes;
using Bazaar.Server.Git.GitLab.Tests.Support;

namespace Bazaar.Server.Git.GitLab.Tests;

public class GitLabMergeExecutorTests
{
    private const string _merge = "/api/v4/projects/group%2Fsub%2Fproject/merge_requests/42/merge";
    private const string _head = "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0";

    private static (GitLabMergeExecutor Executor, FakeGitLab Http, FakeCredentialSource Credentials) Create()
    {
        var http = new FakeGitLab();
        var credentials = new FakeCredentialSource();
        var executor = new GitLabMergeExecutor(new HttpClient(http), credentials, time: new InstantTimeProvider());
        return (executor, http, credentials);
    }

    private static MergeRequest Request(string signOff = "Signed-off-by: The Owner <owner@example.test>") =>
        new(_head, "Add thing (#42)", signOff);

    [Fact]
    public async Task Squash_merge_body_is_pinned_to_the_head_with_the_sign_off_in_the_message()
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"state\":\"merged\",\"iid\":42}");

        await executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None);

        var sent = http.Requests.Single();
        Assert.Equal(HttpMethod.Put, sent.Method);
        var expected = JsonNode.Parse("""
            {
              "sha": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6a7b8c9d0",
              "squash": true,
              "squash_commit_message": "Add thing (#42)\n\nSigned-off-by: The Owner <owner@example.test>",
              "should_remove_source_branch": false
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(sent.Body!)));
    }

    [Fact]
    public async Task Squash_merge_message_is_the_title_alone_when_there_is_no_sign_off()
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"state\":\"merged\"}");

        await executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(signOff: string.Empty), CancellationToken.None);

        var body = JsonNode.Parse(http.Requests.Single().Body!)!.AsObject();
        Assert.Equal("Add thing (#42)", body["squash_commit_message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Merge_uses_a_push_credential_and_disposes_it()
    {
        var (executor, http, credentials) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"state\":\"merged\"}");

        await executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None);

        var lease = Assert.Single(credentials.Leases);
        Assert.Equal(GitOperation.Push, lease.Operation);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
        Assert.Equal(FakeCredentialSource.Secret, http.Requests.Single().Token);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotAcceptable)]
    public async Task A_refused_merge_is_a_typed_refusal_and_is_not_retried(HttpStatusCode status)
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"message\":\"SHA does not match HEAD\"}", status);

        var error = await Assert.ThrowsAsync<GitLabMergeRefusedException>(() =>
            executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_200_that_does_not_report_merged_is_a_refusal()
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{\"state\":\"opened\"}");

        await Assert.ThrowsAsync<GitLabMergeRefusedException>(() =>
            executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None));
    }

    [Fact]
    public async Task Auth_failures_on_merge_are_not_refusals_and_do_not_leak_the_token()
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{}", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<GitLabApiException>(() =>
            executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None));

        Assert.IsNotType<GitLabMergeRefusedException>(error);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain(FakeCredentialSource.Secret, error.ToString());
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_missing_merge_request_on_merge_is_the_neutral_not_found()
    {
        var (executor, http, _) = Create();
        http.Json(HttpMethod.Put, _merge, "{}", HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<ForgeNotFoundException>(() =>
            executor.SquashMergeAsync(new PrRef("gitlab", "group/sub/project", 42), Request(), CancellationToken.None));
    }

    [Fact]
    public void The_executor_reports_gitlab()
    {
        var (executor, _, _) = Create();
        Assert.Equal("gitlab", executor.ProviderId);
    }
}
