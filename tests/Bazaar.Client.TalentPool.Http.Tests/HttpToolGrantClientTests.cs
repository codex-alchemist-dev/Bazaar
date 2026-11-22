using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Approvals;

namespace Bazaar.Client.TalentPool.Http.Tests;

public class HttpToolGrantClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    private static HttpToolGrantClient Client(Stub handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://bazaar.test/") }, () => "tok");

    [Fact]
    public async Task Lists_pending_grants_with_the_bearer_credential()
    {
        var json = JsonSerializer.Serialize(new[] { new ToolGrantCard("code-reviewer", 2, "Code Reviewer", ["shell"]) }, ToolGrantJsonContext.Default.ToolGrantCardArray);
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var cards = await Client(handler).ListPendingAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(cards);
        Assert.Equal("code-reviewer@2", card.HireKey);
        Assert.Equal(["shell"], card.Tools);
        Assert.Equal(("/talent/tool-grants/pending", "Bearer tok"), (handler.Seen[0].Path, handler.Seen[0].Auth));
    }

    [Fact]
    public async Task A_failed_list_throws()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await Client(handler).ListPendingAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Posts_the_signed_grant_to_the_version_route()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var payload = new OwnerPayload("talent.tool_grant", Guid.NewGuid(), [1, 2, 3], null, Guid.NewGuid(), "device-1", now, now.AddMinutes(5), new byte[16]);

        await Client(handler).SendAsync("code-reviewer", 2, payload, [9, 9], TestContext.Current.CancellationToken);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/talent/templates/code-reviewer/versions/2/tool-grant", seen.Path);
        using var doc = JsonDocument.Parse(seen.Body);
        Assert.Equal("talent.tool_grant", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("010203", doc.RootElement.GetProperty("argsHashHex").GetString());
        Assert.Equal(1_800_000_000_000, doc.RootElement.GetProperty("issuedAtUnixMs").GetInt64());
        Assert.Equal(Convert.ToBase64String([9, 9]), doc.RootElement.GetProperty("signatureBase64").GetString());
    }

    [Fact]
    public async Task A_failed_post_throws_and_is_not_retried()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var now = DateTimeOffset.UtcNow;
        var payload = new OwnerPayload("talent.tool_grant", Guid.NewGuid(), [1], null, Guid.NewGuid(), "device-1", now, now.AddMinutes(5), new byte[16]);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await Client(handler).SendAsync("t", 1, payload, [1], TestContext.Current.CancellationToken));
        Assert.Single(handler.Seen);
    }
}
