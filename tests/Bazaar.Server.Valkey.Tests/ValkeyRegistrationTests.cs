using Cdax.Core;
using Bazaar.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Valkey.Tests;

/// <summary>The component manifest, its dependency tags, the DI registration and the option defaults.</summary>
public class ValkeyRegistrationTests
{
    [Fact]
    public void Manifest_is_a_provider_with_no_dependencies_and_the_cache_contract()
    {
        var manifest = new ValkeyComponent().Manifest;

        Assert.Equal("valkey", manifest.Id);
        Assert.Equal(ComponentKind.Provider, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Contains("cache:valkey", manifest.Provides);
    }

    [Fact]
    public void Manifest_tags_come_from_the_one_dependency_tag_source()
    {
        Assert.Equal(["Deps", "3rd party"], ValkeyComponent.Tags);
        Assert.Equal(DependencyTags.Deps, ValkeyComponent.Tags[0]);
        Assert.Equal(DependencyTags.ThirdParty, ValkeyComponent.Tags[1]);
    }

    [Fact]
    public void Options_default_to_an_unmanaged_server_with_safe_values()
    {
        var cache = new ValkeyCacheOptions();
        var supervision = new ValkeySupervisionOptions();

        Assert.False(cache.Managed);
        Assert.Equal("valkey-server", cache.BinaryPath);
        Assert.Equal(0, cache.ManagedPort);
        Assert.Equal(256, cache.MaxMemoryMegabytes);
        Assert.Equal(80, cache.MemoryWarnPercent);
        Assert.Equal("setpriv", cache.ParentDeathWrapper);
        Assert.Equal("allkeys-lru", ValkeyCacheOptions.EvictionPolicy);
        Assert.Equal(TimeSpan.FromSeconds(10), supervision.StartupTimeout);
        Assert.Equal(5, supervision.MaxFailures);
    }

    [Fact]
    public async Task Registration_on_linux_offers_one_cache_provider_a_supervisor_and_the_probes()
    {
        var services = Register(new Dictionary<string, string?> { ["Bazaar:Cache:Valkey:Endpoint"] = "cache.internal:6379" });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsLinux())
        {
            var cache = Assert.Single(provider.GetServices<ICacheProvider>());
            Assert.IsType<ValkeyCacheProvider>(cache);
            Assert.Equal("valkey", cache.Kind);
            Assert.Contains(provider.GetServices<IHostedService>(), h => h is ValkeyServerSupervisor);
            Assert.IsType<ValkeyHealthProbes>(provider.GetRequiredService<IValkeyHealthProbes>());
        }
        else
        {
            Assert.Empty(provider.GetServices<ICacheProvider>());
        }
    }

    [Fact]
    public async Task A_managed_cache_connects_through_the_managed_connection_and_takes_no_endpoint()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["Bazaar:Cache:Valkey:Managed"] = "true",
            ["Bazaar:Cache:Valkey:StartWindowSeconds"] = "1",
        });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsLinux())
        {
            Assert.Null(provider.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value.Endpoint);
            Assert.IsType<ValkeyManagedConnection>(provider.GetRequiredService<IValkeyConnectionSource>());
            var cache = provider.GetServices<ICacheProvider>().Single();
            Assert.False(cache.Probe().Usable, "no child is running yet, so the cache is unusable rather than pointed at a stale address");
        }
    }

    [Fact]
    public void An_unmanaged_cache_connects_to_its_configured_endpoint()
    {
        var services = Register(new Dictionary<string, string?> { ["Bazaar:Cache:Valkey:Endpoint"] = " cache.internal:6379 " });
        using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<ValkeyConfiguredEndpoint>(provider.GetRequiredService<IValkeyConnectionSource>());
        }
    }

    [Fact]
    public async Task The_runtime_directory_is_overridable_by_setting()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["Bazaar:Cache:Valkey:Endpoint"] = "cache.internal:6379",
            ["Bazaar:Cache:Valkey:RuntimeDirectory"] = "/var/lib/bazaar/valkey-test",
        });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("/var/lib/bazaar/valkey-test", provider.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value.RuntimeDirectory);
        }
    }

    private static ServiceCollection Register(IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBazaarValkey(configuration);
        return services;
    }
}
