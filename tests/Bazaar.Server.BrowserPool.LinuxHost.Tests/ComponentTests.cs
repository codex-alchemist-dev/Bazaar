using Cdax.Core;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

public class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceCollection Compose(bool hostFirst, bool isLinux, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        config ??= Config();
        if (hostFirst)
        {
            BrowserPoolLinuxHostComponent.AddServices(services, config, isLinux);
            new BrowserPoolComponent().AddServices(services, config);
        }
        else
        {
            new BrowserPoolComponent().AddServices(services, config);
            BrowserPoolLinuxHostComponent.AddServices(services, config, isLinux);
        }

        return services;
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new BrowserPoolLinuxHostComponent().Manifest;

        Assert.Equal("browser-pool-linux-host", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["browser-pool"], m.DependsOn);
        Assert.Equal(["browser-pool:linux-host"], m.Provides);
        Assert.Equal(BrowserPoolComponent.ManifestDefinition.Id, m.DependsOn[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_host_serves_both_ports_in_either_registration_order(bool hostFirst)
    {
        ServiceCollection services = Compose(hostFirst, isLinux: true);

        // The registrations themselves hold on every OS: one host, and it is the only provider of both ports.
        Assert.Single(services, d => d.ServiceType == typeof(LinuxPoolVmHost));
        Assert.Single(services, d => d.ServiceType == typeof(IPoolVmHost));
        Assert.Single(services, d => d.ServiceType == typeof(IPoolDevToolsPipes));

        if (!OperatingSystem.IsLinux())
        {
            // LinuxPoolVmHost refuses to be constructed off Linux, so the instances can only be resolved on Linux.
            return;
        }

        using ServiceProvider sp = services.BuildServiceProvider();

        IPoolVmHost host = sp.GetRequiredService<IPoolVmHost>();
        IPoolDevToolsPipes pipes = sp.GetRequiredService<IPoolDevToolsPipes>();

        Assert.IsType<LinuxPoolVmHost>(host);
        Assert.Same(host, pipes);
        Assert.Same(host, sp.GetRequiredService<LinuxPoolVmHost>());
        // The pool resolves too, so the replaced host is what it gets.
        Assert.NotNull(sp.GetRequiredService<IBrowserPool>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Off_linux_nothing_is_registered_and_the_pool_keeps_its_fail_closed_host(bool hostFirst)
    {
        ServiceCollection services = Compose(hostFirst, isLinux: false);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPoolDevToolsPipes));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(LinuxPoolVmHost));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(LinuxPoolHostOptions));
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IPoolVmHost>());
        Assert.IsNotType<LinuxPoolVmHost>(sp.GetRequiredService<IPoolVmHost>());
    }

    [Fact]
    public void Off_linux_invalid_configuration_is_not_read()
    {
        var services = new ServiceCollection();

        BrowserPoolLinuxHostComponent.AddServices(services, Config(("Bazaar:BrowserPool:LinuxHost:TableName", "bad name")), isLinux: false);

        Assert.Empty(services);
    }

    [Fact]
    public void The_public_overload_uses_the_real_os()
    {
        var services = new ServiceCollection();

        new BrowserPoolLinuxHostComponent().AddServices(services, Config());

        Assert.Equal(OperatingSystem.IsLinux(), services.Any(d => d.ServiceType == typeof(LinuxPoolVmHost)));
    }

    [Fact]
    public void Options_default_when_the_section_is_absent()
    {
        LinuxPoolHostOptions o = LinuxPoolHostOptions.Bind(Config());

        Assert.Equal("bazaar_pool", o.TableName);
        Assert.Equal("bzv", o.SlotInterfacePrefix);
        Assert.Equal("nft", o.NftPath);
        Assert.Equal(TimeSpan.FromSeconds(60), o.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), o.FirewallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), o.StopTimeout);
        Assert.Equal(16 * 1024, o.MaxCapturedChars);
        Assert.Null(o.PoolSpawnHelperPath);
        Assert.Equal("/run/bazaar-pool/pipes", o.PipeDirectory);
    }

    [Fact]
    public void Options_take_overrides()
    {
        LinuxPoolHostOptions o = LinuxPoolHostOptions.Bind(Config(
            ("Bazaar:BrowserPool:LinuxHost:TableName", "other_table"),
            ("Bazaar:BrowserPool:LinuxHost:SlotInterfacePrefix", ""),
            ("Bazaar:BrowserPool:LinuxHost:NftPath", "/usr/sbin/nft"),
            ("Bazaar:BrowserPool:LinuxHost:CommandTimeout", "00:00:30"),
            ("Bazaar:BrowserPool:LinuxHost:FirewallTimeout", "00:00:05"),
            ("Bazaar:BrowserPool:LinuxHost:StopTimeout", "00:00:02"),
            ("Bazaar:BrowserPool:LinuxHost:MaxCapturedChars", "4096"),
            ("Bazaar:BrowserPool:LinuxHost:PoolSpawnHelperPath", "/opt/bazaar/bazaar-pool-spawn"),
            ("Bazaar:BrowserPool:LinuxHost:PipeDirectory", "/run/bazaar-pool/p")));

        Assert.Equal("other_table", o.TableName);
        Assert.Null(o.SlotInterfacePrefix);
        Assert.Equal("/usr/sbin/nft", o.NftPath);
        Assert.Equal(TimeSpan.FromSeconds(30), o.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), o.FirewallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), o.StopTimeout);
        Assert.Equal(4096, o.MaxCapturedChars);
        Assert.Equal("/opt/bazaar/bazaar-pool-spawn", o.PoolSpawnHelperPath);
        Assert.Equal("/run/bazaar-pool/p", o.PipeDirectory);
    }

    [Theory]
    [InlineData("TableName", "bad name; rm")]
    [InlineData("MaxCapturedChars", "77")]
    [InlineData("MaxCapturedChars", "many")]
    [InlineData("CommandTimeout", "-00:00:01")]
    [InlineData("CommandTimeout", "soon")]
    [InlineData("PoolSpawnHelperPath", "relative/path")]
    public void Invalid_options_throw_without_echoing_the_value(string key, string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => LinuxPoolHostOptions.Bind(Config(($"Bazaar:BrowserPool:LinuxHost:{key}", value))));

        Assert.DoesNotContain(value, ex.Message);
    }

    [Fact]
    public void Invalid_options_stop_the_component_at_startup()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            BrowserPoolLinuxHostComponent.AddServices(services, Config(("Bazaar:BrowserPool:LinuxHost:NftPath", " ")), isLinux: true));
    }
}
