using Bazaar.Community.Drafts;
using Bazaar.Community.Profile;
using Bazaar.Community.Quotas;
using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Community.Tests.Component;

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
    public void Draft_services_register_and_wait_for_the_host_bound_dependencies()
    {
        var services = new ServiceCollection();
        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new CommunityComponent()]);
        using var provider = services.BuildServiceProvider();

        // Without the host's data source, key registry, adapters and policy source, resolving a draft service reports "not configured".
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICommunityDraftStore>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<CommunityDraftDecisionHandler>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICommunityDraftCreator>());
        Assert.NotNull(provider.GetRequiredService<DisclosureOptions>());
        Assert.NotNull(provider.GetRequiredService<CommunityDraftOptions>());
    }

    [Fact]
    public void The_expiry_service_is_a_hosted_service_and_the_sweeper_waits_for_host_dependencies()
    {
        var services = new ServiceCollection();
        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new CommunityComponent()]);
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is CommunityDraftExpiryService);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICommunityDraftExpirySweeper>());
    }

    [Fact]
    public void Draft_store_resolves_over_the_shared_data_source()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=none"));
        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new CommunityComponent()]);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<NpgsqlCommunityDraftStore>(provider.GetRequiredService<ICommunityDraftStore>());
    }
}
