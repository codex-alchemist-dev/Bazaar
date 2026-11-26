using System.Net;
using System.Net.Http.Headers;
using Bazaar.Server.Identity.GitHubApp;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class GitHubAppAgentGitIdentityTests
{
    private sealed class Routed(Func<HttpRequestMessage, (HttpStatusCode, string)> route) : HttpMessageHandler
    {
        public List<(Uri Url, AuthenticationHeaderValue? Auth)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization));
            var (status, body) = route(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Names(string? name) : IGitHubAuthorNames
    {
        public ValueTask<string?> FindAsync(string agentId, CancellationToken cancellationToken) => ValueTask.FromResult(name);
    }

    private static Routed Happy() => new(r => r.RequestUri!.AbsolutePath == "/app"
        ? (HttpStatusCode.OK, """{"slug":"charles-agent","id":5}""")
        : (HttpStatusCode.OK, """{"id":4242,"login":"charles-agent[bot]"}"""));

    private static (GitHubAppAgentGitIdentity Identity, Routed Handler) Build(string? name, Routed? handler = null, bool register = true)
    {
        var rsa = GitHubAppTestSupport.CreateKey();
        var keys = new InMemoryGitHubAppKeyStore();
        if (register)
        {
            keys.Set("charles", new GitHubAppCredentials(77, GitHubAppTestSupport.PemOf(rsa)));
        }

        var h = handler ?? Happy();
        var client = new HttpGitHubAppBotClient(h);
        var signer = new GitHubAppJwtSigner(new FakeTimeProvider(GitHubAppTestSupport.Now));
        return (new GitHubAppAgentGitIdentity(keys, signer, client, new Names(name)), h);
    }

    [Fact]
    public async Task Author_is_display_name_and_bot_noreply_address()
    {
        var (identity, handler) = Build("Charles");

        var author = await identity.GetAuthorAsync("charles", CancellationToken.None);

        Assert.Equal("Charles", author.Name);
        Assert.Equal("4242+charles-agent[bot]@users.noreply.github.com", author.Email);
        Assert.Equal("https://api.github.com/app", handler.Requests[0].Url.ToString());
        Assert.Equal("Bearer", handler.Requests[0].Auth?.Scheme);
        Assert.Equal("https://api.github.com/users/charles-agent%5Bbot%5D", handler.Requests[1].Url.AbsoluteUri);
        Assert.Null(handler.Requests[1].Auth);
    }

    [Fact]
    public async Task Bot_lookup_is_cached_per_agent()
    {
        var (identity, handler) = Build("Charles");

        await identity.GetAuthorAsync("charles", CancellationToken.None);
        await identity.GetAuthorAsync("charles", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unknown_name_throws()
    {
        var (identity, _) = Build(null);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await identity.GetAuthorAsync("charles", CancellationToken.None));
    }

    [Theory]
    [InlineData("Eve <evil@x>")]
    [InlineData("  ")]
    [InlineData("a\nb")]
    public async Task Unsafe_name_throws(string name)
    {
        var (identity, _) = Build(name);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await identity.GetAuthorAsync("charles", CancellationToken.None));
    }

    [Fact]
    public async Task Agent_without_app_throws_key_not_found()
    {
        var (identity, _) = Build("Charles", register: false);
        await Assert.ThrowsAsync<GitHubAppKeyNotFoundException>(async () => await identity.GetAuthorAsync("charles", CancellationToken.None));
    }

    [Fact]
    public async Task Refused_lookup_throws_status_only()
    {
        var handler = new Routed(_ => (HttpStatusCode.Unauthorized, "secret-detail"));
        var (identity, _) = Build("Charles", handler);

        var ex = await Assert.ThrowsAsync<GitHubAppException>(async () => await identity.GetAuthorAsync("charles", CancellationToken.None));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-detail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsafe_slug_is_refused()
    {
        var handler = new Routed(_ => (HttpStatusCode.OK, """{"slug":"../evil","id":1}"""));
        var (identity, _) = Build("Charles", handler);
        await Assert.ThrowsAsync<GitHubAppException>(async () => await identity.GetAuthorAsync("charles", CancellationToken.None));
    }
}
