using System.Net;

namespace Bazaar.Client.SignIn.Tests;

public sealed class WhoAmIClientTests
{
    private static readonly ClientSession _session = new("access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddMinutes(5));
    private static readonly Guid _human = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _device = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (HttpWhoAmIClient Client, FakeHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new FakeHandler((request, _) => answer(request));
        return (new HttpWhoAmIClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") }), handler);
    }

    [Fact]
    public async Task Maps_200_to_the_person_and_sends_the_bearer_token()
    {
        var (client, handler) = Make(_ => FakeHandler.Json(HttpStatusCode.OK,
            $$"""{"human_id":"{{_human}}","display_name":"Ada Lovelace","role":"owner","device_id":"{{_device}}"}"""));

        var result = await client.WhoAmIAsync(_session, TestContext.Current.CancellationToken);

        Assert.Equal(WhoAmIOutcome.Found, result.Outcome);
        Assert.Equal(new SignedInPerson(_human, "Ada Lovelace", "owner", _device), result.Person);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(("GET", "/auth/whoami"), (request.Method, request.Path));
    }

    [Fact]
    public async Task Sends_the_access_token_only_in_the_authorization_header()
    {
        string? header = null;
        string? uri = null;
        var (client, _) = Make(request =>
        {
            header = request.Headers.Authorization?.ToString();
            uri = request.RequestUri?.ToString();
            return FakeHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""");
        });

        await client.WhoAmIAsync(_session, TestContext.Current.CancellationToken);

        Assert.Equal("Bearer access-secret", header);
        Assert.DoesNotContain("access-secret", uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, WhoAmIOutcome.InvalidToken)]
    [InlineData(HttpStatusCode.Forbidden, WhoAmIOutcome.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable, WhoAmIOutcome.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, WhoAmIOutcome.Unavailable)]
    public async Task Maps_refusals(HttpStatusCode status, WhoAmIOutcome expected)
    {
        var (client, _) = Make(_ => new HttpResponseMessage(status));

        var result = await client.WhoAmIAsync(_session, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Person);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"human_id":"00000000-0000-0000-0000-000000000000","display_name":"x","role":"owner","device_id":"22222222-2222-2222-2222-222222222222"}""")]
    [InlineData("""{"human_id":"11111111-1111-1111-1111-111111111111","display_name":"","role":"owner","device_id":"22222222-2222-2222-2222-222222222222"}""")]
    public async Task An_unreadable_or_incomplete_200_is_unavailable(string body)
    {
        var (client, _) = Make(_ => FakeHandler.Json(HttpStatusCode.OK, body));

        var result = await client.WhoAmIAsync(_session, TestContext.Current.CancellationToken);

        Assert.Equal(WhoAmIOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_network_failure_is_unavailable()
    {
        var (client, _) = Make(_ => throw new HttpRequestException("down"));

        var result = await client.WhoAmIAsync(_session, TestContext.Current.CancellationToken);

        Assert.Equal(WhoAmIOutcome.Unavailable, result.Outcome);
    }
}
