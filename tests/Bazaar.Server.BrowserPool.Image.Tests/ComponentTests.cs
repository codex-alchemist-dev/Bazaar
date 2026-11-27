using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.BrowserPool.Image.Tests;

public class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolImageComponent().Manifest;

        Assert.Equal("browser-pool-image", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["browser-pool"], m.DependsOn);
        Assert.Equal(BrowserPoolComponent.ManifestDefinition.Id, m.DependsOn[0]);
    }

    [Fact]
    public void On_linux_the_check_is_registered_and_the_pool_resolves_with_it()
    {
        var services = new ServiceCollection();
        IConfiguration config = Config();
        new BrowserPoolComponent().AddServices(services, config);
        BrowserPoolImageComponent.AddServices(services, config, isLinux: true);

        Assert.Single(services, d => d.ServiceType == typeof(IPoolReadinessCheck));
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.IsType<PoolImageReadinessCheck>(sp.GetRequiredService<IPoolReadinessCheck>());
        Assert.NotNull(sp.GetRequiredService<PoolImageOptions>());
        Assert.NotNull(sp.GetRequiredService<IBrowserPool>());
    }

    [Fact]
    public void Off_linux_nothing_is_registered()
    {
        var services = new ServiceCollection();

        BrowserPoolImageComponent.AddServices(services, Config(), isLinux: false);

        Assert.Empty(services);
    }

    [Fact]
    public void Off_linux_invalid_configuration_is_not_read()
    {
        var services = new ServiceCollection();

        BrowserPoolImageComponent.AddServices(services, Config(("Bazaar:BrowserPool:Image:RecheckInterval", "soon")), isLinux: false);

        Assert.Empty(services);
    }

    [Fact]
    public void Invalid_configuration_stops_the_component_on_linux()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            BrowserPoolImageComponent.AddServices(services, Config(("Bazaar:BrowserPool:Image:RecheckInterval", "soon")), isLinux: true));
    }

    [Fact]
    public void The_public_overload_uses_the_real_os()
    {
        var services = new ServiceCollection();

        new BrowserPoolImageComponent().AddServices(services, Config());

        Assert.Equal(OperatingSystem.IsLinux(), services.Any(d => d.ServiceType == typeof(IPoolReadinessCheck)));
    }
}
