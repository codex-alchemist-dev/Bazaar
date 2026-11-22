using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Approvals.Tests;

public sealed class HttpCommunityLogSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }

    private static ServerEndpoint Endpoint() => new(new Uri("http://127.0.0.1:8080/"), "tok");

    private static HttpResponseMessage Json(CommunityLogPageDto page) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(page, CommunityContractsJsonContext.Default.CommunityLogPageDto), Encoding.UTF8, "application/json"),
    };

    private static CommunityLogRowDto Row(long id) =>
        new(id, "agent-1", "reddit", "Post", "https://x/y", "ab12", "done", DateTimeOffset.UnixEpoch, null, null);

    [Fact]
    public async Task Gets_the_log_route_with_credential_and_escaped_filters()
    {
        var handler = new Handler(_ => Json(new CommunityLogPageDto([Row(2)], "c2")));
        var sut = HttpCommunityLogSource.Create(Endpoint(), handler);

        var page = await sut.LoadAsync(new CommunityLogQueryDto("a b&c=d", "hacker news/é", 25), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:8080/community/log?agentId=a%20b%26c%3Dd&platform=hacker%20news%2F%C3%A9&limit=25", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", request.Headers.Authorization.Parameter);
        Assert.Equal(2, Assert.Single(page.Rows).Id);
        Assert.Equal("c2", page.NextCursor);
    }

    [Fact]
    public void Blank_filters_and_cursor_are_left_out()
    {
        Assert.Equal("community/log?limit=50", HttpCommunityLogSource.BuildRelativeUrl(new CommunityLogQueryDto(null, "", 50, "")));
    }

    [Fact]
    public async Task Cursor_round_trips_untouched_across_pages()
    {
        const string Cursor = "a+b/c=="; // opaque, with characters that need escaping
        var handler = new Handler(request => request.RequestUri!.Query.Contains("cursor=", StringComparison.Ordinal)
            ? Json(new CommunityLogPageDto([Row(1)], null))
            : Json(new CommunityLogPageDto([Row(2)], Cursor)));
        var sut = HttpCommunityLogSource.Create(Endpoint(), handler);

        var first = await sut.LoadAsync(new CommunityLogQueryDto(null, null, 1), CancellationToken.None);
        var second = await sut.LoadAsync(new CommunityLogQueryDto(null, null, 1, first.NextCursor), CancellationToken.None);

        Assert.Equal(Cursor, first.NextCursor);
        Assert.Null(second.NextCursor);
        Assert.Equal("?limit=1&cursor=a%2Bb%2Fc%3D%3D", handler.Requests[1].RequestUri!.Query);
        Assert.Equal(Cursor, Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query.Split("cursor=")[1]));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Refusals_throw_with_the_status_and_no_body(int status)
    {
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET-BODY") });
        var sut = HttpCommunityLogSource.Create(Endpoint(), handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sut.LoadAsync(new CommunityLogQueryDto(null, null, 10), CancellationToken.None));

        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
        Assert.DoesNotContain("SECRET-BODY", ex.Message, StringComparison.Ordinal);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_answer_throws_a_request_exception()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });
        var sut = HttpCommunityLogSource.Create(Endpoint(), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.LoadAsync(new CommunityLogQueryDto(null, null, 10), CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_server_propagates()
    {
        var sut = HttpCommunityLogSource.Create(Endpoint(), new ThrowingHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.LoadAsync(new CommunityLogQueryDto(null, null, 10), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        var handler = new Handler(_ => Json(new CommunityLogPageDto([], null)));
        var sut = HttpCommunityLogSource.Create(Endpoint(), handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.LoadAsync(new CommunityLogQueryDto(null, null, 10), cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task No_credential_sends_no_authorization_header()
    {
        var handler = new Handler(_ => Json(new CommunityLogPageDto([], null)));
        var sut = new HttpCommunityLogSource(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") }, () => null);

        await sut.LoadAsync(new CommunityLogQueryDto(null, null, 10), CancellationToken.None);

        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }
}
