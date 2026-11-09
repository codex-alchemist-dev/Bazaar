using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Blocklist;
using Bazaar.Server.Community.Policy;
using Bazaar.Server.Community.Quotas;
using Bazaar.Server.Components;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Community.Tests.Component;

public class CommunityComponentTests
{
    [Fact]
    public void Manifest_names_the_community_namespace_and_depends_on_nothing()
    {
        var manifest = new CommunityComponent().Manifest;

        Assert.Equal("community", manifest.Id);
        Assert.Equal("community", manifest.MigrationNamespace);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
    }

    [Fact]
    public void Registers_the_freeze_store_when_the_component_is_enabled()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new CommunityComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<InMemoryCommunityFreezeStore>(provider.GetRequiredService<ICommunityFreezeStore>());
    }

    [Fact]
    public void Registers_the_community_feature_services()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new CommunityComponent()]);

        Assert.Contains(services, d => d.ServiceType == typeof(PlatformPolicyLoader));
        Assert.Contains(services, d => d.ServiceType == typeof(ITargetBlocklist));
        Assert.Contains(services, d => d.ServiceType == typeof(ICommunityActionSql));
        Assert.Contains(services, d => d.ServiceType == typeof(CommunityActionStore));
        Assert.Contains(services, d => d.ServiceType == typeof(KillSwitch));
        Assert.Contains(services, d => d.ServiceType == typeof(IRateBucketStore));
        Assert.Contains(services, d => d.ServiceType == typeof(CommunityQuotaGuard));
    }
}
