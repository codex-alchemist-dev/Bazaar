using Bazaar.Secrets;
using Bazaar.Server.Security.Credentials;

namespace Bazaar.Server.Security.Tests.Credentials;

public class LeaseMatcherSecretPolicyTests
{
    private const string _name = "credential.11111111222233334444555555555555";
    private static readonly SecretScope _agent = SecretScope.ForAgent(new AgentId("agent-a"));
    private static readonly SecretPurpose _purpose = new("lease.match");

    private readonly LeaseMatcherSecretPolicy _policy = new(new Inner());

    private Task<bool> Allows(string component = "lease-matcher", SecretScope? scope = null, string? name = _name, string purpose = "lease.match") =>
        _policy.AllowsAsync(component, scope ?? _agent, name is null ? null : new SecretName(name), new SecretPurpose(purpose), CancellationToken.None);

    [Fact]
    public async Task Allows_the_matcher_read_in_any_agent_scope() => Assert.True(await Allows());

    [Fact]
    public async Task Refuses_another_component() => Assert.False(await Allows(component: "gateway"));

    [Fact]
    public async Task Refuses_another_purpose() => Assert.False(await Allows(purpose: "gateway.call"));

    [Fact]
    public async Task Refuses_the_installation_scope() => Assert.False(await Allows(scope: SecretScope.Installation));

    [Fact]
    public async Task Refuses_names_that_are_not_credential_ids()
    {
        Assert.False(await Allows(name: "providers.anthropic.key"));
        Assert.False(await Allows(name: "credential.1111"));
        Assert.False(await Allows(name: "credential.1111111122223333444455555555555g"));
        Assert.False(await Allows(name: null));
    }

    [Fact]
    public async Task Everything_else_goes_to_the_inner_policy()
    {
        Assert.True(await Allows(component: "inner-ok", name: "providers.x"));
    }

    private sealed class Inner : ISecretPolicy
    {
        public Task<bool> AllowsAsync(string component, SecretScope scope, SecretName? name, SecretPurpose purpose, CancellationToken ct) =>
            Task.FromResult(component == "inner-ok");
    }
}
