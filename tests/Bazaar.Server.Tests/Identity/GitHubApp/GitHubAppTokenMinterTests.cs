using Bazaar.Server.Identity.GitHubApp;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class GitHubAppTokenMinterTests
{
    private static GitHubAppTokenRequest Request(params string[] repositories) =>
        new(77, repositories, GitHubAppTestSupport.Permissions(("contents", "write")), TimeSpan.FromMinutes(10));

    private static (GitHubAppTokenMinter Minter, GitHubAppTestSupport.FakeInstallationClient Client, string Pem) Build(
        IGitHubLeaseScope scope,
        GitHubInstallationToken result,
        DateTimeOffset now,
        string agent = "agent-a")
    {
        var rsa = GitHubAppTestSupport.CreateKey();
        var pem = GitHubAppTestSupport.PemOf(rsa);
        var time = new FakeTimeProvider(now);
        var keys = new InMemoryGitHubAppKeyStore();
        keys.Set(agent, new GitHubAppCredentials(555, pem));
        var client = new GitHubAppTestSupport.FakeInstallationClient(result);
        var minter = new GitHubAppTokenMinter(keys, new GitHubAppJwtSigner(time), client, scope, time);
        return (minter, client, pem);
    }

    [Fact]
    public async Task Narrowed_request_inside_lease_is_minted_with_the_agents_app_jwt()
    {
        var (minter, client, _) = Build(
            new GitHubAppTestSupport.StaticLeaseScope("Bazaar", "Docs"),
            new GitHubInstallationToken("ghs_minted", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now);

        var token = await minter.MintAsync("agent-a", Request("Bazaar"), CancellationToken.None);

        var call = Assert.Single(client.Calls);
        Assert.Equal(77, call.InstallationId);
        Assert.Equal(["Bazaar"], call.Request.Repositories);
        Assert.Equal(3, call.Jwt.Split('.').Length);
        Assert.Equal("ghs_minted", token.Token);
    }

    [Fact]
    public async Task Returned_expiry_is_capped_at_the_requested_lifetime()
    {
        var (minter, _, _) = Build(
            new GitHubAppTestSupport.StaticLeaseScope("Bazaar"),
            new GitHubInstallationToken("ghs_minted", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now);

        var token = await minter.MintAsync("agent-a", Request("Bazaar"), CancellationToken.None);

        Assert.Equal(GitHubAppTestSupport.Now.AddMinutes(10), token.ExpiresAt);
    }

    [Fact]
    public async Task Repository_outside_the_lease_is_refused_before_any_key_is_read()
    {
        var (minter, client, _) = Build(
            new GitHubAppTestSupport.StaticLeaseScope("Bazaar"),
            new GitHubInstallationToken("ghs_never", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now);

        var ex = await Assert.ThrowsAsync<GitHubAppScopeException>(() =>
            minter.MintAsync("agent-a", Request("Bazaar", "Secrets"), CancellationToken.None).AsTask());

        Assert.Contains("Secrets", ex.Message);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Empty_lease_refuses_every_request()
    {
        var (minter, client, _) = Build(
            new GitHubAppTestSupport.StaticLeaseScope(),
            new GitHubInstallationToken("ghs_never", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now);

        await Assert.ThrowsAsync<GitHubAppScopeException>(() =>
            minter.MintAsync("agent-a", Request("Bazaar"), CancellationToken.None).AsTask());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Repository_match_ignores_case()
    {
        var (minter, _, _) = Build(
            new GitHubAppTestSupport.StaticLeaseScope("bazaar"),
            new GitHubInstallationToken("ghs_minted", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now);

        var token = await minter.MintAsync("agent-a", Request("Bazaar"), CancellationToken.None);

        Assert.Equal("ghs_minted", token.Token);
    }

    [Fact]
    public async Task Key_store_miss_throws_and_never_leaks_the_pem()
    {
        var (minter, client, pem) = Build(
            new GitHubAppTestSupport.StaticLeaseScope("Bazaar"),
            new GitHubInstallationToken("ghs_never", GitHubAppTestSupport.Now.AddHours(1)),
            GitHubAppTestSupport.Now,
            agent: "agent-a");

        var ex = await Assert.ThrowsAsync<GitHubAppKeyNotFoundException>(() =>
            minter.MintAsync("agent-missing", Request("Bazaar"), CancellationToken.None).AsTask());

        Assert.Empty(client.Calls);
        Assert.DoesNotContain("PRIVATE KEY", ex.ToString());
        Assert.DoesNotContain(pem.Split('\n')[1], ex.ToString());
    }

    [Fact]
    public void Credentials_and_minted_token_redact_their_secrets_in_ToString()
    {
        var creds = new GitHubAppCredentials(555, "-----BEGIN PRIVATE KEY-----\nSECRETBODY\n-----END PRIVATE KEY-----");
        var token = new GitHubInstallationToken("ghs_plaintext_value", GitHubAppTestSupport.Now);

        Assert.DoesNotContain("SECRETBODY", creds.ToString());
        Assert.DoesNotContain("PRIVATE KEY", creds.ToString());
        Assert.DoesNotContain("ghs_plaintext_value", token.ToString());
    }
}
