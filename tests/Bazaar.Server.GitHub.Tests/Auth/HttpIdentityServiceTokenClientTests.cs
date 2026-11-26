using System.Net;
using System.Net.Http;
using System.Text;
using Bazaar.Server.GitHub.Auth;

namespace Bazaar.Server.GitHub.Tests.Auth;

public sealed class HttpIdentityServiceTokenClientTests
{
    private static HttpClient Client(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return new HttpClient(handler) { BaseAddress = new Uri("https://identity.invalid") };
    }

    private static GitHubTokenScope Scope() => new(7, [1L], new Dictionary<string, string> { ["contents"] = "read" });

    [Fact]
    public async Task Success_body_maps_to_token_and_expiry()
    {
        var http = Client(HttpStatusCode.OK, """{"token":"ghs_abc","expiresAt":"2026-10-30T13:00:00Z"}""");
        var client = new HttpIdentityServiceTokenClient(http, new IdentityServiceGitHubTokenOptions());

        var response = await client.ExchangeAsync(Scope(), CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("ghs_abc", response.Token);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero), response.ExpiresAt);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public async Task Error_body_maps_to_status_and_error_code_only()
    {
        var http = Client(HttpStatusCode.Unauthorized, """{"error":"bad_scope"}""");
        var client = new HttpIdentityServiceTokenClient(http, new IdentityServiceGitHubTokenOptions());

        var response = await client.ExchangeAsync(Scope(), CancellationToken.None);

        Assert.Equal(401, response.StatusCode);
        Assert.Null(response.Token);
        Assert.Equal("bad_scope", response.ErrorCode);
    }

    [Fact]
    public async Task Non_json_error_body_keeps_the_status()
    {
        var http = Client(HttpStatusCode.BadGateway, "<html>gateway</html>");
        var client = new HttpIdentityServiceTokenClient(http, new IdentityServiceGitHubTokenOptions());

        var response = await client.ExchangeAsync(Scope(), CancellationToken.None);

        Assert.Equal(502, response.StatusCode);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public async Task Transport_failure_surfaces_as_token_exception()
    {
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://identity.invalid") };
        var provider = new IdentityServiceGitHubTokenProvider(
            new HttpIdentityServiceTokenClient(http, new IdentityServiceGitHubTokenOptions()));

        var ex = await Assert.ThrowsAsync<GitHubTokenException>(() => provider.GetTokenAsync(Scope(), CancellationToken.None));

        Assert.Null(ex.StatusCode);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }
}
