using System.Net;
using System.Text.Json;
using Bazaar.Diagnostics.Grouping;
using Bazaar.Server.Diagnostics.Adapters;

namespace Bazaar.Server.Diagnostics.Tests.Adapters;

public sealed class GitHubFilerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body, string? Auth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            var (status, json) = reply(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json) };
        }
    }

    private sealed class Token : ICrashGitHubTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("ghs_testtoken");
    }

    private static GitHubCrashIssueFiler Filer(StubHandler handler, string repo = "codex/bazaar") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, new Token(), new CrashFilingOptions { IssueRepo = repo });

    [Fact]
    public async Task Creating_an_issue_posts_title_and_body_to_the_one_repo_and_returns_the_number()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.Created, "{\"number\":42}"));

        var number = await Filer(handler).CreateIssueAsync(new IssueText("T", "B <!-- bazaar-crash-fp: fp1:x -->"), TestContext.Current.CancellationToken);

        Assert.Equal(42, number);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/repos/codex/bazaar/issues", call.Path);
        Assert.Equal("Bearer ghs_testtoken", call.Auth);
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("T", doc.RootElement.GetProperty("title").GetString());
        Assert.Contains("bazaar-crash-fp", doc.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reopening_sets_the_state_open_then_comments()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));

        await Filer(handler).ReopenAsync(7, "Back in 0.2.0.", TestContext.Current.CancellationToken);

        Assert.Equal([(HttpMethod.Patch, "/repos/codex/bazaar/issues/7"), (HttpMethod.Post, "/repos/codex/bazaar/issues/7/comments")], handler.Calls.Select(c => (c.Method, c.Path)));
        Assert.Contains("\"state\":\"open\"", handler.Calls[0].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task A_missing_issue_raises_not_found_so_the_group_can_refile(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => (status, "{}"));

        var ex = await Assert.ThrowsAsync<CrashIssueNotFoundException>(() => Filer(handler).CommentAsync(9, "x", TestContext.Current.CancellationToken));

        Assert.Equal(9, ex.IssueNumber);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Limits_and_server_errors_are_retryable_failures_not_a_missing_issue(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => (status, "{}"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Filer(handler).CommentAsync(9, "x", TestContext.Current.CancellationToken));

        Assert.Equal(status, ex.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("../evil")]
    public async Task A_malformed_repository_setting_never_reaches_the_network(string repo)
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Filer(handler, repo).CommentAsync(1, "x", TestContext.Current.CancellationToken));

        Assert.Empty(handler.Calls);
    }
}
