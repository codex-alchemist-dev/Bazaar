using Bazaar.Core;
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
    public void Manifest_carries_the_deps_and_third_party_tags()
    {
        Assert.Equal(["Deps", "3rd party"], ValkeyComponent.Tags);
    }

    [Fact]
    public void Options_default_to_an_unmanaged_server_with_safe_values()
    {
        var cache = new ValkeyCacheOptions();
        var supervision = new ValkeySupervisionOptions();

        Assert.False(cache.Managed);
        Assert.Equal("valkey-server", cache.BinaryPath);
        Assert.Equal(16379, cache.ManagedPort);
        Assert.Equal(80, cache.MemoryWarnPercent);
        Assert.Equal(TimeSpan.FromSeconds(10), supervision.StartupTimeout);
        Assert.Equal(5, supervision.MaxFailures);
    }

    [Fact]
    public void Launch_arguments_bind_loopback_without_persistence_or_secrets()
    {
        var spec = new ValkeyLaunchSpec("valkey-server", 16379);

        Assert.Equal(
            ["--bind", "127.0.0.1", "--port", "16379", "--protected-mode", "yes", "--save", string.Empty, "--appendonly", "no"],
            spec.Arguments);
    }

    [Fact]
    public async Task Registration_on_linux_offers_one_cache_provider_and_a_supervisor()
    {
        var services = Register(new Dictionary<string, string?> { ["Bazaar:Cache:Valkey:Endpoint"] = "cache.internal:6379" });
        await using var provider = services.BuildServiceProvider();

        if (OperatingSystem.IsLinux())
        {
            var cache = Assert.Single(provider.GetServices<ICacheProvider>());
            Assert.IsType<ValkeyCacheProvider>(cache);
            Assert.Equal("valkey", cache.Kind);
            Assert.Contains(provider.GetServices<IHostedService>(), h => h is ValkeyServerSupervisor);
        }
        else
        {
            Assert.Empty(provider.GetServices<ICacheProvider>());
        }
    }

    [Fact]
    public void A_managed_child_without_an_endpoint_is_reached_on_loopback()
    {
        var services = Register(new Dictionary<string, string?> { ["Bazaar:Cache:Valkey:Managed"] = "true" });
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value;

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("127.0.0.1:16379", options.Endpoint);
        }
        else
        {
            Assert.Null(options.Endpoint);
        }
    }

    [Fact]
    public void An_explicit_endpoint_wins_over_the_managed_loopback_default()
    {
        var services = Register(new Dictionary<string, string?>
        {
            ["Bazaar:Cache:Valkey:Managed"] = "true",
            ["Bazaar:Cache:Valkey:Endpoint"] = "cache.internal:6379",
        });
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ValkeyCacheOptions>>().Value;

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("cache.internal:6379", options.Endpoint);
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
