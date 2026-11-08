using Bazaar.Server.Community.Quotas;
using Bazaar.Server.Components;
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
}
