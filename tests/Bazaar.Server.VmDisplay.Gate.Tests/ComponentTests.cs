using Cdax.Core;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.Gate.Tests;

public class ComponentTests
{
    private static readonly DisplayTarget _target = new("agent-a", Guid.NewGuid(), DisplayGuestOs.Windows, "backend-x");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(bool gateFirst, Action<IServiceCollection>? host = null, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        config ??= Config();
        if (gateFirst)
        {
            new VmDisplayGateComponent().AddServices(services, config);
            new VmDisplayComponent().AddServices(services, config);
        }
        else
        {
            new VmDisplayComponent().AddServices(services, config);
            new VmDisplayGateComponent().AddServices(services, config);
        }

        host?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void AddReaders(IServiceCollection s)
    {
        s.AddSingleton<IKillSwitchReader>(new FakeKillSwitch());
        s.AddSingleton<IAgentSessionTaintReader>(new FakeTaint());
        s.AddSingleton<ILeasedCredentialMatcher>(new FakeMatcher());
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new VmDisplayGateComponent().Manifest;

        Assert.Equal("vm-display-gate", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["vm-display"], m.DependsOn);
        Assert.Equal(["vm-display:gate"], m.Provides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_gate_replaces_the_refusing_default_in_either_order(bool gateFirst)
    {
        using ServiceProvider sp = Build(gateFirst, AddReaders);

        Assert.IsType<SecurityDisplayGate>(sp.GetRequiredService<IDisplayGate>());
        Assert.Single(sp.GetServices<IDisplayGate>());
    }

    [Fact]
    public async Task Missing_readers_make_the_gate_deny_without_throwing()
    {
        using ServiceProvider sp = Build(true, config: Config(("Bazaar:VmDisplayGate:Enabled", "true")));

        IDisplayGate gate = sp.GetRequiredService<IDisplayGate>();

        Assert.Equal(SecurityDisplayGate.ReasonReadsUnavailable, (await gate.CheckCaptureAsync(_target, TestContext.Current.CancellationToken)).Reason);
        Assert.False((await gate.CheckInputAsync(_target, [new KeyStroke("Tab")], TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task The_gate_is_on_by_default_and_refuses_everything_when_switched_off()
    {
        using ServiceProvider on = Build(true, AddReaders);
        using ServiceProvider off = Build(true, AddReaders, Config(("Bazaar:VmDisplayGate:Enabled", "false")));

        Assert.Equal(SecurityDisplayGate.ReasonNotEnabled, (await off.GetRequiredService<IDisplayGate>().CheckCaptureAsync(_target, TestContext.Current.CancellationToken)).Reason);
        Assert.False((await off.GetRequiredService<IDisplayGate>().CheckInputAsync(_target, [new KeyStroke("Tab")], TestContext.Current.CancellationToken)).Allowed);
        Assert.True((await on.GetRequiredService<IDisplayGate>().CheckCaptureAsync(_target, TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public void A_later_host_gate_still_wins()
    {
        var custom = new SecurityDisplayGate(new DisplayGateOptions(), null, null, null);

        using ServiceProvider sp = Build(false, s => s.AddSingleton<IDisplayGate>(custom));

        Assert.Same(custom, sp.GetRequiredService<IDisplayGate>());
    }

    [Fact]
    public void The_component_registers_no_security_readers()
    {
        using ServiceProvider sp = Build(true);

        Assert.Null(sp.GetService<IKillSwitchReader>());
        Assert.Null(sp.GetService<IAgentSessionTaintReader>());
        Assert.Null(sp.GetService<ILeasedCredentialMatcher>());
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("InputUnderHardTaint", "none")]
    [InlineData("InputUnderHardTaint", "AccountBrowsers")]
    [InlineData("InputUnderHardTaint", "")]
    [InlineData("ReadTimeoutSeconds", "0")]
    [InlineData("ReadTimeoutSeconds", "61")]
    [InlineData("ReadTimeoutSeconds", "five")]
    public void Bad_options_fail_at_start(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Build(true, config: Config(("Bazaar:VmDisplayGate:" + key, value))));
    }

    [Fact]
    public void Options_bind_from_the_section_and_default_to_the_strict_side()
    {
        DisplayGateOptions o = DisplayGateOptions.Bind(Config(
            ("Bazaar:VmDisplayGate:Enabled", "true"),
            ("Bazaar:VmDisplayGate:InputUnderHardTaint", "approval"),
            ("Bazaar:VmDisplayGate:ReadTimeoutSeconds", "9")));

        Assert.True(o.Enabled);
        Assert.Equal(InputUnderHardTaint.Approval, o.InputUnderHardTaint);
        Assert.Equal(9, o.ReadTimeoutSeconds);

        DisplayGateOptions d = DisplayGateOptions.Bind(Config());
        Assert.True(d.Enabled);
        Assert.Equal(InputUnderHardTaint.AccountBrowsers, d.InputUnderHardTaint);
        Assert.Equal(5, d.ReadTimeoutSeconds);
        Assert.Equal(InputUnderHardTaint.All, DisplayGateOptions.Bind(Config(("Bazaar:VmDisplayGate:InputUnderHardTaint", "all"))).InputUnderHardTaint);
    }

    [Fact]
    public void Direct_construction_rejects_bad_options()
    {
        Assert.Throws<InvalidOperationException>(() => new SecurityDisplayGate(new DisplayGateOptions { ReadTimeoutSeconds = 0 }, null, null, null));
        Assert.Throws<InvalidOperationException>(() => new SecurityDisplayGate(new DisplayGateOptions { InputUnderHardTaint = (InputUnderHardTaint)99 }, null, null, null));
    }
}
