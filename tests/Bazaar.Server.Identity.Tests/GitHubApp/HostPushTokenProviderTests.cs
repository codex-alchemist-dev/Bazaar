using System.Reflection;
using Bazaar.Server.Identity.GitHubApp;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Identity.GitHubApp;

public class HostPushTokenProviderTests
{
    private static HostPushTokenProvider Build(GitHubInstallationToken result, out GitHubAppTestSupport.FakeInstallationClient client)
    {
        var rsa = GitHubAppTestSupport.CreateKey();
        var time = new FakeTimeProvider(GitHubAppTestSupport.Now);
        var keys = new InMemoryGitHubAppKeyStore();
        keys.Set("agent-a", new GitHubAppCredentials(555, GitHubAppTestSupport.PemOf(rsa)));
        client = new GitHubAppTestSupport.FakeInstallationClient(result);
        var minter = new GitHubAppTokenMinter(keys, new GitHubAppJwtSigner(time), client, new GitHubAppTestSupport.StaticLeaseScope("Bazaar"), time);
        return new HostPushTokenProvider(minter);
    }

    [Fact]
    public async Task Push_credential_is_scoped_to_one_repository_with_contents_write()
    {
        var provider = Build(new GitHubInstallationToken("ghs_push", GitHubAppTestSupport.Now.AddHours(1)), out var client);

        var credential = await provider.GetPushCredentialAsync("agent-a", 77, "Bazaar", CancellationToken.None);

        var call = Assert.Single(client.Calls);
        Assert.Equal(["Bazaar"], call.Request.Repositories);
        Assert.Equal("write", call.Request.Permissions["contents"]);
        Assert.Single(call.Request.Permissions);
        Assert.Equal(HostPushTokenProvider.PushLifetime, call.Request.Lifetime);
        Assert.Equal("Bazaar", credential.Repository);
        Assert.Equal("ghs_push", credential.Token);
    }

    [Fact]
    public async Task Push_credential_for_repository_outside_the_lease_is_refused()
    {
        var provider = Build(new GitHubInstallationToken("ghs_push", GitHubAppTestSupport.Now.AddHours(1)), out var client);

        await Assert.ThrowsAsync<GitHubAppScopeException>(() =>
            provider.GetPushCredentialAsync("agent-a", 77, "Other", CancellationToken.None).AsTask());
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task Push_credential_has_no_path_or_file_output()
    {
        var provider = Build(new GitHubInstallationToken("ghs_push", GitHubAppTestSupport.Now.AddHours(1)), out _);
        var credential = await provider.GetPushCredentialAsync("agent-a", 77, "Bazaar", CancellationToken.None);

        var members = credential.GetType()
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => member.Name)
            .ToArray();

        Assert.DoesNotContain(members, name => name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("File", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Write", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("Save", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Push_credential_ToString_redacts_the_token()
    {
        var provider = Build(new GitHubInstallationToken("ghs_push_secret_value", GitHubAppTestSupport.Now.AddHours(1)), out _);
        var credential = await provider.GetPushCredentialAsync("agent-a", 77, "Bazaar", CancellationToken.None);

        var text = credential.ToString();

        Assert.DoesNotContain("ghs_push_secret_value", text);
        Assert.Contains("[redacted]", text);
        Assert.Contains("Bazaar", text);
    }
}
