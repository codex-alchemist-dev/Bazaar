using Cdax.Core;
using Bazaar.Server.Providers;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>The component manifest, its dependency tags and the Windows-only DI registration.</summary>
public class GarnetRegistrationTests
{
    [Fact]
    public void Manifest_is_a_provider_with_no_dependencies_and_the_cache_contract()
    {
        var manifest = new GarnetComponent().Manifest;

        Assert.Equal("garnet", manifest.Id);
        Assert.Equal(ComponentKind.Provider, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["cache:garnet"], manifest.Provides);
    }

    [Fact]
    public void Manifest_tags_come_from_the_dependency_tag_source()
    {
        Assert.Equal(["Deps", "3rd party"], GarnetComponent.Tags);
    }

    [Fact]
    public async Task Registration_offers_one_cache_provider_on_windows_and_none_elsewhere()
    {
        var services = Register(new Dictionary<string, string?> { ["Bazaar:Cache:Garnet:Endpoint"] = "cache.internal:6379" });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsWindows())
        {
            var cache = Assert.Single(provider.GetServices<ICacheProvider>());
            Assert.IsType<GarnetCacheProvider>(cache);
            Assert.Equal("garnet", cache.Kind);
            Assert.Contains(provider.GetServices<IHostedService>(), h => h is GarnetServerSupervisor);
            Assert.IsType<GarnetHealthProbes>(provider.GetRequiredService<IValkeyHealthProbes>());
            Assert.IsType<GarnetConfiguredEndpoint>(provider.GetRequiredService<IValkeyConnectionSource>());
        }
        else
        {
            Assert.Empty(provider.GetServices<ICacheProvider>());
            Assert.Empty(provider.GetServices<IHostedService>());
        }
    }

    [Fact]
    public async Task A_managed_cache_uses_the_managed_connection_and_is_unusable_until_the_child_runs()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["Bazaar:Cache:Garnet:Managed"] = "true",
            ["Bazaar:Cache:Garnet:StartWindowSeconds"] = "1",
        });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<GarnetManagedConnection>(provider.GetRequiredService<IValkeyConnectionSource>());
            Assert.False(provider.GetServices<ICacheProvider>().Single().Probe().Usable);
        }
    }

    private static ServiceCollection Register(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBazaarGarnet(configuration);
        return services;
    }
}
