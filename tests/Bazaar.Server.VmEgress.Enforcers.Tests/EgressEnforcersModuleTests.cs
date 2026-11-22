using Bazaar.Server.VmEgress;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmEgress.Enforcers.Tests;

public class EgressEnforcersModuleTests
{
    private static readonly EgressTarget _target = new(["vm1"], "tap0");

    private static ServiceCollection Services(bool withResolver = true)
    {
        var services = new ServiceCollection();
        if (withResolver)
        {
            services.AddSingleton<IEgressTargetResolver>(new FakeResolver(_target));
        }

        return services;
    }

    [Fact]
    public void Windows_gets_the_hyperv_enforcer()
    {
        var services = Services().AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);

        using var sp = services.BuildServiceProvider();

        Assert.IsType<HyperVEgressEnforcer>(sp.GetRequiredService<IEgressEnforcer>());
    }

    [Fact]
    public void Linux_gets_the_nftables_enforcer()
    {
        var services = Services().AddBazaarVmEgressEnforcers(EnforcerPlatform.Linux);

        using var sp = services.BuildServiceProvider();

        Assert.IsType<NftablesEgressEnforcer>(sp.GetRequiredService<IEgressEnforcer>());
    }

    [Fact]
    public void The_public_overload_picks_by_the_running_os()
    {
        var services = Services().AddBazaarVmEgressEnforcers();

        using var sp = services.BuildServiceProvider();

        var enforcer = sp.GetRequiredService<IEgressEnforcer>();
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<HyperVEgressEnforcer>(enforcer);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<NftablesEgressEnforcer>(enforcer);
        }
        else
        {
            Assert.IsType<UnavailableEgressEnforcer>(enforcer);
        }
    }

    [Fact]
    public void An_unsupported_platform_stays_fail_closed()
    {
        var services = Services().AddBazaarVmEgressEnforcers(EnforcerPlatform.Unsupported);

        using var sp = services.BuildServiceProvider();

        Assert.IsType<UnavailableEgressEnforcer>(sp.GetRequiredService<IEgressEnforcer>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void It_replaces_the_unavailable_default_whichever_order_the_modules_are_added(bool enforcersFirst)
    {
        var services = Services();
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new NullAudit());
        if (enforcersFirst)
        {
            services.AddBazaarVmEgressEnforcers(EnforcerPlatform.Linux);
            services.AddBazaarVmEgress();
        }
        else
        {
            services.AddBazaarVmEgress();
            services.AddBazaarVmEgressEnforcers(EnforcerPlatform.Linux);
        }

        using var sp = services.BuildServiceProvider();

        Assert.Single(services, d => d.ServiceType == typeof(IEgressEnforcer));
        Assert.IsType<NftablesEgressEnforcer>(sp.GetRequiredService<IEgressEnforcer>());
    }

    [Fact]
    public async Task Without_a_resolver_resolving_works_but_every_call_throws()
    {
        var services = Services(withResolver: false).AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);
        using var sp = services.BuildServiceProvider();

        var enforcer = sp.GetRequiredService<IEgressEnforcer>();

        Assert.IsType<UnavailableEgressEnforcer>(enforcer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => enforcer.BlockAllAsync("a1"));
    }

    [Fact]
    public void Default_runners_are_registered_and_a_host_override_wins()
    {
        var own = new FakeHostRunner();
        var services = Services();
        services.AddSingleton<IHostCommandRunner>(own);
        services.AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);

        using var sp = services.BuildServiceProvider();

        Assert.Same(own, sp.GetRequiredService<IHostCommandRunner>());
        Assert.IsType<NftRootCommandRunner>(sp.GetRequiredService<IRootCommandRunner>());
        using var plain = Services().AddBazaarVmEgressEnforcers(EnforcerPlatform.Linux).BuildServiceProvider();
        Assert.IsType<ProcessHostCommandRunner>(plain.GetRequiredService<IHostCommandRunner>());
    }

    [Fact]
    public async Task The_hyperv_enforcer_from_di_drives_the_registered_runner()
    {
        var runner = new HyperVSim();
        var services = Services();
        services.AddSingleton<IHostCommandRunner>(runner);
        services.AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<IEgressEnforcer>().ApplyAsync("a1", Samples.Rules());

        Assert.Equal(6, runner.AclsOf("vm1").Count);
    }

    private sealed class NullAudit : Bazaar.Server.Security.Audit.IAuditLog
    {
        public Task AppendAsync(Bazaar.Server.Security.Audit.AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
