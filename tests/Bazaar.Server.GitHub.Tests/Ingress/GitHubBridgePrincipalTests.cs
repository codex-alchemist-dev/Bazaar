using Bazaar.Server.GitHub.Ingress;

namespace Bazaar.Server.GitHub.Tests.Ingress;

public sealed class GitHubBridgePrincipalTests
{
    [Fact]
    public void Derive_matches_the_RFC_9562_version_5_reference_vector()
    {
        // RFC 4122/9562 DNS namespace, name "www.example.com": 2ed6657d-e927-568b-95e1-2665a8aea6a2.
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"), GitHubBridgePrincipal.Derive(dns, "www.example.com"));
    }

    [Fact]
    public void The_bridge_id_is_stable_versioned_and_not_empty()
    {
        Assert.Equal(GitHubBridgePrincipal.Derive(GitHubBridgePrincipal.Namespace, GitHubBridgePrincipal.Name), GitHubBridgePrincipal.Id);
        Assert.NotEqual(Guid.Empty, GitHubBridgePrincipal.Id);
        Assert.Equal('5', GitHubBridgePrincipal.Id.ToString()[14]);
    }

    [Fact]
    public void The_override_wins_and_an_empty_override_falls_back_to_the_seeded_id()
    {
        var custom = Guid.NewGuid();

        Assert.Equal(GitHubBridgePrincipal.Id, new GitHubIngressOptions().EffectiveBridgePrincipalId);
        Assert.Equal(GitHubBridgePrincipal.Id, new GitHubIngressOptions { BridgePrincipalId = Guid.Empty }.EffectiveBridgePrincipalId);
        Assert.Equal(custom, new GitHubIngressOptions { BridgePrincipalId = custom }.EffectiveBridgePrincipalId);
    }
}
