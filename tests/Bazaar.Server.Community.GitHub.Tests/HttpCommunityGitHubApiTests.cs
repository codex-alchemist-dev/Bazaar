using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bazaar.Server.Community.GitHub.Tests;

public sealed class HttpCommunityGitHubApiTests : IDisposable
{
    private const string _token = "ghs_SECRETTOKEN123";
    private readonly ScriptedHandler _handler = new();

    public void Dispose() => _handler.Dispose();

    private HttpCommunityGitHubApi Api(ICommunityGitHubTokenSource? tokens = null) =>
        new(new HttpClient(_handler), tokens ?? new FixedTokenSource(_token));

    [Fact]
    public async Task Create_issue_posts_title_and_body_with_bearer_token()
    {
        _handler.Enqueue(HttpStatusCode.Created, Fixtures.IssueCreated);

        var item = await Api().CreateIssueAsync("acme", "widgets", "T", "B", CancellationToken.None);

        Assert.Equal("2001", item.ExternalId);
        Assert.Equal("https://github.com/acme/widgets/issues/42", item.Url.ToString());
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/acme/widgets/issues", request.Uri.ToString());
        Assert.Equal("Bearer " + _token, request.Authorization);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("T", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("B", json.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Create_comment_posts_to_the_issue_comments_endpoint()
    {
        _handler.Enqueue(HttpStatusCode.Created, Fixtures.CommentCreated);

        var item = await Api().CreateCommentAsync("acme", "widgets", 7, "hi", CancellationToken.None);

        Assert.Equal("4001", item.ExternalId);
        Assert.EndsWith("/repos/acme/widgets/issues/7/comments", Assert.Single(_handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Find_in_search_returns_the_item_whose_body_carries_the_exact_marker()
    {
        _handler.Enqueue(HttpStatusCode.OK, Fixtures.SearchFound);
        var marker = GitHubCommunityAdapter.MarkerFor(Guid.Parse("11111111-2222-3333-4444-555555555555"));

        var item = await Api().FindByMarkerAsync("acme", "widgets", null, marker, CancellationToken.None);

        Assert.Equal("2001", item?.ExternalId);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/search/issues", request.Uri.AbsolutePath);
        Assert.Contains("repo:acme/widgets", Uri.UnescapeDataString(request.Uri.Query));
    }

    [Fact]
    public async Task Find_in_search_ignores_a_near_miss_and_an_empty_result()
    {
        var marker = GitHubCommunityAdapter.MarkerFor(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        _handler.Enqueue(HttpStatusCode.OK, Fixtures.SearchNearMiss).Enqueue(HttpStatusCode.OK, Fixtures.SearchEmpty);

        Assert.Null(await Api().FindByMarkerAsync("acme", "widgets", null, marker, CancellationToken.None));
        Assert.Null(await Api().FindByMarkerAsync("acme", "widgets", null, marker, CancellationToken.None));
    }

    [Fact]
    public async Task Find_in_comments_scans_the_issue_comments()
    {
        _handler.Enqueue(HttpStatusCode.OK, Fixtures.CommentsFound);
        var marker = GitHubCommunityAdapter.MarkerFor(Guid.Parse("11111111-2222-3333-4444-555555555555"));

        var item = await Api().FindByMarkerAsync("acme", "widgets", 7, marker, CancellationToken.None);

        Assert.Equal("4001", item?.ExternalId);
        Assert.StartsWith("/repos/acme/widgets/issues/7/comments", Assert.Single(_handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Adapter_over_http_creates_then_reuses()
    {
        var api = Api();
        var adapter = new GitHubCommunityAdapter(api, new RecordingPullRequestOpener(), new DefaultCommunityFooterCheck());
        _handler
            .Enqueue(HttpStatusCode.OK, Fixtures.SearchEmpty)
            .Enqueue(HttpStatusCode.Created, Fixtures.IssueCreated)
            .Enqueue(HttpStatusCode.OK, Fixtures.SearchFound);

        var first = await adapter.PostAsync(TestSupport.Post(), TestSupport.Approval, CancellationToken.None);
        var second = await adapter.PostAsync(TestSupport.Post(), TestSupport.Approval, CancellationToken.None);

        Assert.False(first.Reused);
        Assert.True(second.Reused);
        using var sent = JsonDocument.Parse(_handler.Requests[1].Body);
        Assert.EndsWith("-->", sent.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Too_many_requests_is_retryable_and_carries_retry_after()
    {
        _handler.Enqueue(HttpStatusCode.TooManyRequests, """{"message":"slow down"}""", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42)));

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.Equal(42, ex.Data[HttpCommunityGitHubApi.RetryAfterDataKey]);
        Assert.Contains("42", ex.Message);
    }

    [Fact]
    public async Task Secondary_rate_limit_403_is_retryable()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, Fixtures.SecondaryRateLimit, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.Equal(60, ex.Data[HttpCommunityGitHubApi.RetryAfterDataKey]);
    }

    [Fact]
    public async Task Secondary_rate_limit_403_without_header_is_still_retryable()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, Fixtures.SecondaryRateLimit);

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Plain_403_permission_failure_is_not_retryable()
    {
        _handler.Enqueue(HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Server_errors_are_retryable(HttpStatusCode status)
    {
        _handler.Enqueue(status, "<html>oops</html>");

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Not_found_is_not_retryable()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, Fixtures.NotFound);

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.False(ex.Retryable);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Validation_failure_422_is_not_retryable()
    {
        _handler.Enqueue(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Error_messages_never_contain_the_token_or_authorization_header()
    {
        _handler
            .Enqueue(HttpStatusCode.NotFound, Fixtures.NotFound)
            .Enqueue(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)))
            .Enqueue(HttpStatusCode.BadGateway, "bad");

        for (var i = 0; i < 3; i++)
        {
            var ex = await Assert.ThrowsAsync<CommunityPostException>(() => Api().CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));
            Assert.DoesNotContain(_token, ex.ToString());
            Assert.DoesNotContain("Bearer", ex.ToString());
            Assert.DoesNotContain("Authorization", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Transport_failure_is_retryable_and_hides_the_request()
    {
        var api = new HttpCommunityGitHubApi(new HttpClient(new ThrowingHandler()), new FixedTokenSource(_token));

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => api.CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.DoesNotContain(_token, ex.ToString());
    }

    [Fact]
    public async Task Token_source_failure_is_retryable_and_does_not_send_a_request()
    {
        var api = Api(new FailingTokenSource());

        var ex = await Assert.ThrowsAsync<CommunityPostException>(() => api.CreateIssueAsync("a", "b", "t", "b", CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.Empty(_handler.Requests);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class FailingTokenSource : ICommunityGitHubTokenSource
    {
        public Task<string> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("identity service down");
    }
}
