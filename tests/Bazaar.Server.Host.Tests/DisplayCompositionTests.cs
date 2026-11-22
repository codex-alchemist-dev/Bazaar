using Bazaar.Core;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.BrowserPool;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.Audit;
using Bazaar.Server.BrowserPool.Egress;
using Bazaar.Server.BrowserPool.Gate;
using Bazaar.Server.BrowserPool.LinuxHost;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmDisplay.Audit;
using Bazaar.Server.VmDisplay.Gate;
using Bazaar.Server.VmDisplay.HyperV;
using Bazaar.Server.VmDisplay.Kvm;
using Microsoft.Extensions.Configuration;
using Bazaar.Server.VmEgress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// The host loads the display, density and browser pool components (docs/design/59, work package 3). Every port the host does not
/// supply keeps the module's fail-closed default, and these tests prove that through the resolved services, not only through options.
/// </summary>
public sealed class DisplayCompositionTests
{
    private static readonly string[] _componentIds =
    [
        "vm-display", "vm-display-gate", "vm-display-audit", "vm-display-agent-tools", "vm-display-hyperv", "vm-display-kvm",
        "vm-density", "browser-pool", "browser-pool-display", "browser-pool-linux-host", "browser-pool-image",
        "browser-pool-gate", "browser-pool-audit", "browser-pool-egress",
    ];

    private static readonly DisplayTarget _target = new("agent-one", Guid.NewGuid(), DisplayGuestOs.Linux, "vm-one");

    [Fact]
    public void Every_display_density_and_pool_component_is_listed_once_and_after_its_dependencies()
    {
        var manifests = HostComponents.All.Select(c => c.Manifest).ToList();
        foreach (var id in _componentIds)
        {
            Assert.Single(manifests, m => m.Id == id);
        }

        var ordered = ComponentGraph.Order(manifests).Select(m => m.Id).ToList();
        foreach (var manifest in manifests.Where(m => _componentIds.Contains(m.Id)))
        {
            foreach (var dependency in manifest.DependsOn)
            {
                Assert.True(ordered.IndexOf(dependency) < ordered.IndexOf(manifest.Id), $"{dependency} must come before {manifest.Id}.");
            }
        }

        // The listed order is the registration order, so the dependency also comes first in the list itself.
        var listed = manifests.Select(m => m.Id).ToList();
        Assert.True(listed.IndexOf("vm-display") < listed.IndexOf("vm-display-gate"));
        Assert.True(listed.IndexOf("vm-display") < listed.IndexOf("vm-display-audit"));
        Assert.True(listed.IndexOf("browser-pool") < listed.IndexOf("browser-pool-display"));
        Assert.True(listed.IndexOf("browser-pool") < listed.IndexOf("browser-pool-linux-host"));
        Assert.True(listed.IndexOf("browser-pool") < listed.IndexOf("browser-pool-image"));
        Assert.True(listed.IndexOf("browser-pool") < listed.IndexOf("browser-pool-egress"));
        Assert.True(listed.IndexOf("browser-pool-gate") < listed.IndexOf("browser-pool-egress"));
        Assert.True(listed.IndexOf("browser-pool-audit") < listed.IndexOf("browser-pool-egress"));
    }

    [Fact]
    public async Task With_default_configuration_the_real_gate_has_every_reader_and_refuses_while_the_stop_state_cannot_be_read()
    {
        using var host = BuildHost();
        var display = host.Services.GetRequiredService<IGuestDisplay>();

        // The gate is the real one, not the display module's unconfigured default.
        Assert.IsType<SecurityDisplayGate>(host.Services.GetRequiredService<IDisplayGate>());
        // The host registers all three security readers. This test host has no database, so the kill switch state is never
        // loaded, which counts as frozen, and the gate refuses. With a live database the gate decides per call (frozen, tainted, leased credential).
        Assert.NotNull(host.Services.GetService<IKillSwitchReader>());
        Assert.NotNull(host.Services.GetService<IAgentSessionTaintReader>());
        Assert.NotNull(host.Services.GetService<ILeasedCredentialMatcher>());

        var capture = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(_target), CancellationToken.None));
        Assert.Equal(DisplayFailure.Refused, capture.Failure);
        Assert.Equal("Display capture refused: agent is frozen.", capture.Message);

        var batch = new InputBatch(_target, [new KeyStroke("Enter")]);
        var input = await Assert.ThrowsAsync<DisplayException>(() => display.SendInputAsync(batch, CancellationToken.None));
        Assert.Equal(DisplayFailure.Refused, input.Failure);
        Assert.Equal("Display input refused: agent is frozen.", input.Message);

        // Nothing is known about any display size until a capture succeeds.
        Assert.Null(host.Services.GetRequiredService<IDisplayNativeSizeReader>().TryGetNativeSize(_target));
        // The audit sink is the audit log's, not the module's no-op sink.
        Assert.IsType<AuditLogDisplayAuditSink>(host.Services.GetRequiredService<IDisplayAuditSink>());
    }

    [Fact]
    public async Task The_gate_can_be_switched_off_by_configuration_and_then_refuses_everything()
    {
        using var host = BuildHost(
            new Dictionary<string, string?> { ["Bazaar:VmDisplayGate:Enabled"] = "false" },
            services => services.AddSingleton<ILeasedCredentialMatcher>(new InMemoryLeasedCredentialMatcher()));
        var display = host.Services.GetRequiredService<IGuestDisplay>();

        var capture = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(_target), CancellationToken.None));

        Assert.Equal(DisplayFailure.Refused, capture.Failure);
        Assert.Equal("Display capture refused: display gate not enabled.", capture.Message);
    }

    [Fact]
    public async Task With_every_security_reader_present_the_gate_allows_and_the_call_then_fails_for_lack_of_a_source()
    {
        using var host = BuildHost(services =>
        {
            services.AddSingleton<ILeasedCredentialMatcher>(new InMemoryLeasedCredentialMatcher());
            services.AddSingleton<IKillSwitchReader>(new FixedKillSwitch(frozen: false));
        });
        var display = host.Services.GetRequiredService<IGuestDisplay>();

        var capture = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(_target), CancellationToken.None));

        // The gate said yes; no source serves a VM target that no hypervisor knows (there is no guest source and no browser session).
        Assert.NotEqual(DisplayFailure.Refused, capture.Failure);
        Assert.NotEqual(DisplayFailure.Disabled, capture.Failure);
    }

    [Fact]
    public async Task A_frozen_kill_switch_refuses_capture_and_input_through_the_host_graph()
    {
        using var host = BuildHost(services =>
        {
            services.AddSingleton<ILeasedCredentialMatcher>(new InMemoryLeasedCredentialMatcher());
            services.AddSingleton<IKillSwitchReader>(new FixedKillSwitch(frozen: true));
        });
        var display = host.Services.GetRequiredService<IGuestDisplay>();

        var capture = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(_target), CancellationToken.None));
        Assert.Equal(DisplayFailure.Refused, capture.Failure);
        Assert.Equal("Display capture refused: agent is frozen.", capture.Message);

        var input = await Assert.ThrowsAsync<DisplayException>(() => display.SendInputAsync(new InputBatch(_target, [new KeyStroke("Enter")]), CancellationToken.None));
        Assert.Equal(DisplayFailure.Refused, input.Failure);
        Assert.Equal("Display input refused: agent is frozen.", input.Message);
    }

    private sealed class FixedKillSwitch(bool frozen) : IKillSwitchReader
    {
        public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken ct = default) => ValueTask.FromResult(frozen);

        public ValueTask<KillSwitchDecision> EvaluateAsync(string agentId, ToolEffect effect, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task The_display_module_can_be_switched_off_by_configuration()
    {
        using var host = BuildHost(new Dictionary<string, string?> { ["Bazaar:VmDisplay:Enabled"] = "false" });
        var display = host.Services.GetRequiredService<IGuestDisplay>();

        var capture = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(_target), CancellationToken.None));
        Assert.Equal(DisplayFailure.Disabled, capture.Failure);
        Assert.Empty(display.AvailableSources(_target));
    }

    [Fact]
    public void Hypervisor_adapters_follow_the_operating_system_and_no_guest_exec_or_browser_driver_serves_a_vm_target()
    {
        using var host = BuildHost();
        var consoles = host.Services.GetServices<IHypervisorConsole>().Select(c => c.GetType()).ToList();

        Assert.Contains(typeof(HyperVConsole), consoles);
        Assert.Equal(OperatingSystem.IsLinux(), consoles.Contains(typeof(KvmConsole)));
        // The guest channel has no host adapter yet, so there is no guest source.
        Assert.Empty(host.Services.GetServices<IDisplayGuestExec>());
        // The pool's page driver exists but serves nothing: no session has a DevTools pipe.
        Assert.All(host.Services.GetServices<IBrowserPageDriver>(), driver => Assert.False(driver.Supports(_target with { BrowserSessionId = "no-such-session" })));
    }

    [Fact]
    public void The_screen_tools_are_not_offered_until_the_host_supplies_the_agent_display_targets()
    {
        using var host = BuildHost();

        Assert.DoesNotContain(host.Services.GetServices<ToolRegistration>(), t => t.Name is "screen.capture" or "screen.input");
    }

    [Fact]
    public async Task The_density_governor_resolves_and_admits_everything_until_the_vm_ports_exist()
    {
        using var host = BuildHost();
        var governor = host.Services.GetRequiredService<IDensityGovernor>();

        Assert.False(Assert.IsType<DensityGovernor>(governor).IsActive);
        await using var ticket = await governor.AdmitAsync(new DensityVm(Guid.NewGuid(), "agent-one", DensityGuestOs.Linux, 2048), CancellationToken.None);
        Assert.Equal(0, governor.Snapshot().Running);
        // The memory probe is the one density port the host can supply today.
        Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), host.Services.GetService<IHostMemoryProbe>() is not null);
    }

    [Fact]
    public async Task The_browser_pool_resolves_and_refuses_every_session_by_default()
    {
        using var host = BuildHost();
        var pool = host.Services.GetRequiredService<IBrowserPool>();

        var failure = await Assert.ThrowsAsync<BrowserPoolException>(() => pool.OpenAsync(new PoolSessionRequest("agent-one"), CancellationToken.None));

        Assert.Equal(PoolFailure.AgentBlocked, failure.Failure);
        var gate = Assert.IsType<KillSwitchPoolGate>(host.Services.GetRequiredService<IPoolGate>());
        Assert.Same(gate, host.Services.GetRequiredService<IAsyncPoolGate>());
        Assert.IsType<AuditLogPoolAuditSink>(host.Services.GetRequiredService<IPoolAuditSink>());
        Assert.IsType<AllowlistStorePoolEgressPolicy>(host.Services.GetRequiredService<IPoolEgressPolicy>());
        Assert.Empty(pool.Sessions("agent-one"));
    }

    [Fact]
    public async Task The_pool_egress_policy_answers_from_the_egress_store_and_has_no_proxy_for_an_agent_nobody_set_up()
    {
        using var host = BuildHost();
        var policy = host.Services.GetRequiredService<IPoolEgressPolicy>();

        Assert.IsType<AllowlistStorePoolEgressPolicy>(policy);
        // No database is needed: the default store is in memory and empty, and nothing starts a proxy at load.
        Assert.Null(await policy.ResolveAsync("agent-one", CancellationToken.None));

        var store = host.Services.GetRequiredService<IEgressAllowlistStore>();
        await store.SaveAsync("agent-one", new EgressRules([], System.Net.IPAddress.Parse("10.20.0.1"), 3128), CancellationToken.None);
        Assert.Equal(new PoolEgressEndpoint("10.20.0.1", 3128), await policy.ResolveAsync("agent-one", CancellationToken.None));
        Assert.Null(await policy.ResolveAsync("agent-two", CancellationToken.None));
    }

    [Fact]
    public void The_pool_vm_host_and_image_check_load_on_linux_only()
    {
        using var host = BuildHost();
        var vmHost = host.Services.GetRequiredService<IPoolVmHost>();

        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxPoolVmHost>(vmHost);
            Assert.Single(host.Services.GetServices<IPoolReadinessCheck>());
        }
        else
        {
            Assert.IsType<UnconfiguredPoolVmHost>(vmHost);
            Assert.Empty(host.Services.GetServices<IPoolReadinessCheck>());
        }
    }

    private static IHost BuildHost(Action<IServiceCollection> configureServices) => BuildHost(null, configureServices);

    private static IHost BuildHost(Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configureServices = null)
    {
        // Process-wide and unique to this class: the secrets resolve when a service first needs them, after Build returns.
        const string dbEnv = "BAZAAR_HOST_DISPLAY_TEST_DB";
        const string tokenEnv = "BAZAAR_HOST_DISPLAY_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-graph-test");

        var values = new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-display-" + Guid.NewGuid().ToString("N")),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
            ["Bazaar:DevAuth:Enabled"] = "true",
            ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
        };
        foreach (var (key, value) in extra ?? [])
        {
            values[key] = value;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddBazaarHostApplication(builder.Configuration);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}
