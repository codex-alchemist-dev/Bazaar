using Bazaar.Secrets;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class SecretStoreCompositionTests
{
    [Fact]
    public void DefaultKekIsNoneAndRegistersNoStore()
    {
        var services = new ServiceCollection();

        services.AddBazaarSecretStore(new ConfigurationBuilder().Build());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ISecretStore));
    }

    [Theory]
    [InlineData("dpapi-ng")]
    [InlineData("tpm")]
    public void ReservedKekChoicesFailInsteadOfFallingBack(string kek)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SecretStoreCompositionExtensions.OpenKek(new SecretStoreOptions { Kek = kek }));

        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKekValueFailsTheStart()
    {
        Assert.Throws<InvalidOperationException>(() => SecretStoreCompositionExtensions.OpenKek(new SecretStoreOptions { Kek = "keychain-maybe" }));
    }

    [Fact]
    public async Task GrantsAreDenyByDefaultAndExactlyScoped()
    {
        var options = new SecretStoreOptions
        {
            Grants =
            [
                new SecretGrantOptions { Component = "gateway", Scope = "agent:alpha", Purpose = "gateway.call" },
            ],
        };

        var policy = SecretStoreCompositionExtensions.BuildPolicy(options);

        Assert.True(await policy.AllowsAsync("gateway", SecretScope.ForAgent(new AgentId("alpha")), new SecretName("token"), new SecretPurpose("gateway.call"), CancellationToken.None));
        Assert.False(await policy.AllowsAsync("gateway", SecretScope.Installation, new SecretName("token"), new SecretPurpose("gateway.call"), CancellationToken.None));
        Assert.False(await policy.AllowsAsync("gateway", SecretScope.ForAgent(new AgentId("alpha")), new SecretName("token"), new SecretPurpose("secrets.manage"), CancellationToken.None));
    }

    [Fact]
    public void MalformedGrantFailsTheStart()
    {
        var options = new SecretStoreOptions
        {
            Grants = [new SecretGrantOptions { Component = "gateway", Scope = "everyone", Purpose = "gateway.call" }],
        };

        Assert.Throws<InvalidOperationException>(() => SecretStoreCompositionExtensions.BuildPolicy(options));
    }
}
