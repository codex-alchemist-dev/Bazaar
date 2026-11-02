using System.Net;
using Bazaar.Server.Identity.GitHubApp;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class HttpGitHubInstallationTokenClientTests
{
    private const string _tokenResponse = """{"token":"ghs_test_value","expires_at":"2026-10-30T13:00:00Z"}""";

    [Fact]
    public async Task Posts_narrowed_repositories_and_permissions_with_bearer_jwt()
    {
        var handler = new GitHubAppTestSupport.RecordingHandler(HttpStatusCode.Created, _tokenResponse);
        using var client = new HttpGitHubInstallationTokenClient(handler);
        var request = new GitHubAppTokenRequest(
            99,
            ["Bazaar"],
            GitHubAppTestSupport.Permissions(("contents", "write")),
            TimeSpan.FromMinutes(10));

        var token = await client.CreateInstallationTokenAsync(99, "jwt.value.sig", request, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.github.com/app/installations/99/access_tokens", sent.Url.ToString());
        Assert.Equal("Bearer", sent.Authorization?.Scheme);
        Assert.Equal("jwt.value.sig", sent.Authorization?.Parameter);
        Assert.Equal("""{"repositories":["Bazaar"],"permissions":{"contents":"write"}}""", sent.Body);
        Assert.Equal("ghs_test_value", token.Token);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture), token.ExpiresAt);
    }

    [Fact]
    public async Task Refused_request_throws_with_status_only()
    {
        var handler = new GitHubAppTestSupport.RecordingHandler(HttpStatusCode.Forbidden, """{"message":"secret-detail-xyz"}""");
        using var client = new HttpGitHubInstallationTokenClient(handler);
        var request = new GitHubAppTokenRequest(99, ["Bazaar"], GitHubAppTestSupport.Permissions(("contents", "read")), TimeSpan.FromMinutes(5));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() =>
            client.CreateInstallationTokenAsync(99, "jwt", request, CancellationToken.None).AsTask());

        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("secret-detail-xyz", ex.Message);
    }

    [Fact]
    public async Task Custom_base_address_is_used_with_or_without_trailing_slash()
    {
        var handler = new GitHubAppTestSupport.RecordingHandler(HttpStatusCode.Created, _tokenResponse);
        using var client = new HttpGitHubInstallationTokenClient(handler, new Uri("https://ghe.example.test/api/v3"));
        var request = new GitHubAppTokenRequest(5, ["Bazaar"], GitHubAppTestSupport.Permissions(("contents", "read")), TimeSpan.FromMinutes(5));

        await client.CreateInstallationTokenAsync(5, "jwt", request, CancellationToken.None);

        Assert.Equal("https://ghe.example.test/api/v3/app/installations/5/access_tokens", handler.Requests[0].Url.ToString());
    }

    [Fact]
    public void Relative_base_address_is_refused()
    {
        var handler = new GitHubAppTestSupport.RecordingHandler(HttpStatusCode.Created, _tokenResponse);

        Assert.Throws<ArgumentException>(() => new HttpGitHubInstallationTokenClient(handler, new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public async Task Response_without_token_is_refused()
    {
        var handler = new GitHubAppTestSupport.RecordingHandler(HttpStatusCode.Created, """{"expires_at":"2026-10-30T13:00:00Z"}""");
        using var client = new HttpGitHubInstallationTokenClient(handler);
        var request = new GitHubAppTokenRequest(5, ["Bazaar"], GitHubAppTestSupport.Permissions(("contents", "read")), TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<GitHubAppException>(() =>
            client.CreateInstallationTokenAsync(5, "jwt", request, CancellationToken.None).AsTask());
    }
}
