using System.Text.Json;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Settings;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmEgress.Approvals;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Canaries;
using Bazaar.Server.VmMounts.Providers.Guest;
using Bazaar.Server.VmMounts.Providers.Local;
using Bazaar.Server.VmMounts.Providers.Smb;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Host.Tests;

public sealed class VmSecurityCompositionTests : IDisposable
{
    private const string _dbEnv = "BAZAAR_HOST_VMSEC_TEST_DB";
    private const string _tokenEnv = "BAZAAR_HOST_VMSEC_TEST_TOKEN";

    public VmSecurityCompositionTests()
    {
        Environment.SetEnvironmentVariable(_dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(_tokenEnv, "dev-secret-for-graph-test");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_dbEnv, null);
        Environment.SetEnvironmentVariable(_tokenEnv, null);
    }

    private static ServiceProvider Build(Dictionary<string, string?>? extra = null) => Build(extra, out _);

    // The whole host graph is not ValidateOnBuild-clean for reasons outside the VM modules (for example the Council hire gate needs
    // ICouncilService, which no host registration provides), so ValidateOnBuild stays off and the VM and agent-safety descriptors
    // are validated one by one in The_vm_and_agent_safety_descriptors_all_resolve.
    private static ServiceProvider Build(Dictionary<string, string?>? extra, out IServiceCollection services, Action<IServiceCollection>? before = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        var values = new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-vmsec-" + Guid.NewGuid().ToString("N")),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _dbEnv,
            ["Bazaar:DevAuth:Enabled"] = "true",
            ["Bazaar:DevAuth:TokenSecret"] = "env:" + _tokenEnv,
        };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        builder.Configuration.AddInMemoryCollection(values);
        before?.Invoke(builder.Services);
        builder.Services.AddBazaarHostApplication(builder.Configuration);
        services = builder.Services;
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void The_vm_and_agent_safety_descriptors_all_resolve()
    {
        var sp = Build(null, out var services);
        string[] prefixes = ["Bazaar.Server.VmEgress", "Bazaar.Server.VmTransport", "Bazaar.Server.VmMounts", "Bazaar.Server.AgentSafety", "Bazaar.Server.Security.KillSwitch"];

        var checkedCount = 0;
        foreach (var d in services.Where(d =>
            !d.ServiceType.ContainsGenericParameters && prefixes.Any(p => (d.ServiceType.Namespace ?? "").StartsWith(p, StringComparison.Ordinal))))
        {
            Assert.NotNull(sp.GetRequiredService(d.ServiceType));
            checkedCount++;
        }

        Assert.True(checkedCount > 10, $"Only {checkedCount} descriptors matched; the prefixes no longer cover the VM modules.");
    }

    [Fact]
    public void The_vm_ports_are_the_real_types()
    {
        var sp = Build();

        Assert.IsType<EgressPolicyService>(sp.GetRequiredService<IEgressPolicy>());
        Assert.IsType<EgressApprovalGate>(sp.GetRequiredService<IEgressApprovalGate>());
        Assert.IsType<InMemoryEgressAllowlistStore>(sp.GetRequiredService<IEgressAllowlistStore>());
        Assert.IsType<NetworkAndMountContainment>(sp.GetRequiredService<IAgentContainment>());
        Assert.NotNull(sp.GetRequiredService<GuestChannelFactory>());
        Assert.NotNull(sp.GetRequiredService<IGuestConnectionListener>());
        Assert.Contains(sp.GetServices<ISettingContribution>(), c => c is VmTransportSettingContribution);
    }

    private sealed class FakeVmDirectory(params EnvironmentVmRecord[] records) : IAgentVmDirectory
    {
        public Task<IReadOnlyList<EnvironmentVmRecord>> ListAsync(Guid agentId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>(records);
    }

    [Fact]
    public void The_channel_directory_is_composed_and_the_mount_provider_reads_it()
    {
        var sp = Build(new() { ["VmMounts:AgentsRoot"] = Path.GetTempPath() });

        Assert.NotNull(sp.GetRequiredService<IGuestChannelDirectory>());
        Assert.NotNull(sp.GetRequiredService<GuestChannelRegistrar>());
        Assert.IsType<RepositoryAgentVmLookup>(sp.GetRequiredService<IAgentVmLookup>());
        Assert.IsType<DirectoryGuestChannelProvider>(sp.GetRequiredService<IGuestChannelProvider>());
    }

    [Fact]
    public async Task The_host_lookup_maps_environment_records_and_marks_retired_vms()
    {
        var now = DateTimeOffset.UnixEpoch;
        EnvironmentVmRecord Record(VmOs os, string? backend, VmState state) =>
            new(Guid.NewGuid(), Guid.NewGuid(), os, Guid.NewGuid(), backend, state, 2, 2048, 20, now, now);
        var lookup = new RepositoryAgentVmLookup(new FakeVmDirectory(
            Record(VmOs.Linux, "bazaar-a", VmState.Running),
            Record(VmOs.Windows, "bazaar-b", VmState.Retired),
            Record(VmOs.Windows, null, VmState.Provisioning)));

        var vms = await lookup.ListAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal([GuestOs.Linux, GuestOs.Windows, GuestOs.Windows], vms.Select(v => v.Os));
        Assert.Equal([false, true, false], vms.Select(v => v.Retired));
        Assert.Equal(["bazaar-a", "bazaar-b", null], vms.Select(v => v.VmName));
        Assert.All(vms, v => Assert.Null(v.GuestInterface));
    }

    private sealed class FakeRunningVms(params EnvironmentVmRecord[] records) : IRunningVmSource
    {
        public Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>([.. records.Where(r => r.State != VmState.Retired)]);

        public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) =>
            Task.FromResult(records.FirstOrDefault(r => r.Id == vmId));
    }

    private sealed class FakeNonces(Dictionary<Guid, string> nonces, Guid? failFor = null) : IGuestNonceSource
    {
        public ValueTask<string?> GetAsync(Guid vmId, CancellationToken ct) =>
            vmId == failFor ? throw new InvalidOperationException("nonce store down") : ValueTask.FromResult(nonces.GetValueOrDefault(vmId));
    }

    [Fact]
    public async Task The_open_targets_map_only_running_vms_with_a_name_and_a_nonce()
    {
        var now = DateTimeOffset.UnixEpoch;
        var agent = Guid.NewGuid();
        EnvironmentVmRecord Record(Guid id, VmOs os, string? backend, VmState state) =>
            new(id, agent, os, Guid.NewGuid(), backend, state, 2, 2048, 20, now, now);
        var linux = Guid.NewGuid();
        var windows = Guid.NewGuid();
        var stopped = Guid.NewGuid();
        var unnamed = Guid.NewGuid();
        var badName = Guid.NewGuid();
        var noNonce = Guid.NewGuid();
        var broken = Guid.NewGuid();
        var nonceLinux = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var nonceWindows = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var targets = new RunningVmGuestOpenTargets(
            new FakeRunningVms(
                Record(linux, VmOs.Linux, "bazaar-a", VmState.Running),
                Record(windows, VmOs.Windows, "bazaar-b", VmState.Running),
                Record(stopped, VmOs.Linux, "bazaar-c", VmState.Stopped),
                Record(unnamed, VmOs.Linux, null, VmState.Running),
                Record(badName, VmOs.Linux, "../x", VmState.Running),
                Record(noNonce, VmOs.Linux, "bazaar-d", VmState.Running),
                Record(broken, VmOs.Linux, "bazaar-e", VmState.Running)),
            new FakeNonces(
                new() { [linux] = nonceLinux, [windows] = nonceWindows, [stopped] = "x", [unnamed] = "x", [badName] = "x" },
                failFor: broken));
        var ct = TestContext.Current.CancellationToken;

        var listed = await targets.ListRunningAsync(ct);

        Assert.Equal([linux, windows], listed.Select(t => t.VmId));
        Assert.Equal([GuestOs.Linux, GuestOs.Windows], listed.Select(t => t.Os));
        Assert.Equal(["bazaar-a", "bazaar-b"], listed.Select(t => t.BackendRef));
        Assert.All(listed, t => Assert.Equal(agent.ToString("D"), t.AgentId));
        Assert.Equal([nonceLinux, nonceWindows], listed.Select(t => t.Nonce));

        Assert.Equal(nonceLinux, (await targets.GetAsync(linux, ct))!.Nonce);
        Assert.Null(await targets.GetAsync(stopped, ct));
        Assert.Null(await targets.GetAsync(noNonce, ct));
        Assert.Null(await targets.GetAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public void Without_a_nonce_source_the_channel_opener_is_not_registered()
    {
        var sp = Build(null, out var services);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGuestOpenTargets));
        Assert.DoesNotContain(sp.GetServices<IHostedService>(), s => s is GuestChannelOpener);
    }

    [Fact]
    public void With_a_nonce_source_the_channel_opener_is_registered()
    {
        Build(null, out var services, s => s.AddSingleton<IGuestNonceSource>(new FakeNonces([])));

        Assert.Contains(services, d => d.ServiceType == typeof(IGuestOpenTargets));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GuestChannelOpener));
    }

    [Fact]
    public void Gaps_resolve_to_their_fail_closed_defaults()
    {
        var sp = Build();

        Assert.IsType<UnavailableEgressEnforcer>(sp.GetRequiredService<IEgressEnforcer>());
        Assert.IsType<UnavailableVmPauser>(sp.GetRequiredService<IVmPauser>());
        Assert.IsType<UnavailableGuestScrubber>(sp.GetRequiredService<IGuestScrubber>());
        Assert.Null(sp.GetService<IMountManager>());
        Assert.Null(sp.GetService<VmLifecycleService>());
    }

    [Fact]
    public async Task The_egress_gate_denies_growth_with_the_fail_closed_gaps()
    {
        var sp = Build();
        var gate = sp.GetRequiredService<IEgressApprovalGate>();

        var state = await gate.RequestGrowthAsync("agent-1", ["example.test"], 2, TestContext.Current.CancellationToken);

        Assert.Equal(EgressApprovalState.Denied, state);
    }

    [Fact]
    public async Task Containment_reports_the_missing_legs_loudly_instead_of_passing()
    {
        var sp = Build();
        var containment = sp.GetRequiredService<IAgentContainment>();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            containment.ContainAsync(Guid.NewGuid().ToString(), "owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Disabling_the_mounts_component_keeps_mounts_out_even_when_configured()
    {
        var sp = Build(new()
        {
            ["Bazaar:Components:vm-mounts:Enabled"] = "false",
            ["VmMounts:AgentsRoot"] = Path.GetTempPath(),
        });

        Assert.Null(sp.GetService<IMountManager>());
    }

    [Fact]
    public void Configuring_a_mounts_root_registers_the_provider_ports_and_composes_the_mount_manager()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-host-mounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sp = Build(new() { ["VmMounts:AgentsRoot"] = root });

            Assert.IsType(
                OperatingSystem.IsWindows() ? typeof(SmbMountProvider) : typeof(DirectoryMountProvider), sp.GetRequiredService<IMountProvider>());
            Assert.IsType<GuestChannelMountAdapter>(sp.GetRequiredService<IMountGuestChannel>());
            Assert.IsType<ContainingCanarySink>(sp.GetRequiredService<ICanarySink>());
            Assert.NotNull(sp.GetRequiredService<IMountManager>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_a_mounts_root_no_provider_port_is_registered()
    {
        var sp = Build();

        Assert.Null(sp.GetService<IMountProvider>());
        Assert.Null(sp.GetService<IMountGuestChannel>());
        Assert.Null(sp.GetService<ICanarySink>());
    }

    [Fact]
    public void Every_vm_mount_descriptor_resolves_once_a_mounts_root_is_configured()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-host-mounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sp = Build(new() { ["VmMounts:AgentsRoot"] = root }, out var services);

            var checkedCount = 0;
            foreach (var d in services.Where(d =>
                !d.ServiceType.ContainsGenericParameters && (d.ServiceType.Namespace ?? "").StartsWith("Bazaar.Server.VmMounts", StringComparison.Ordinal)))
            {
                Assert.NotNull(sp.GetRequiredService(d.ServiceType));
                checkedCount++;
            }

            Assert.True(checkedCount > 15, $"Only {checkedCount} mount descriptors matched.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_invalid_transport_guid_falls_back_to_the_default_with_a_problem()
    {
        using var bad = JsonDocument.Parse("\"not-a-guid\"");
        var options = HvSocketListenerOptions.FromSettings(bad.RootElement, out var problem);

        Assert.Equal(HvSocketListenerOptions.Default.ServiceId, options.ServiceId);
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task The_settings_backed_listener_reads_the_stored_guid_once_on_first_wait()
    {
        var custom = Guid.NewGuid();
        var created = new List<HvSocketListenerOptions>();
        await using var listener = new SettingBackedHvSocketListener(
            new FixedStore(custom.ToString("D")), NullLogger<SettingBackedHvSocketListener>.Instance,
            o => { created.Add(o); return new StubListener(); });

        await listener.AcceptAsync("vm", TestContext.Current.CancellationToken);
        await listener.AcceptAsync("vm", TestContext.Current.CancellationToken);

        Assert.Equal(custom, Assert.Single(created).ServiceId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task The_settings_backed_listener_falls_back_for_an_invalid_stored_guid(string stored)
    {
        var created = new List<HvSocketListenerOptions>();
        await using var listener = new SettingBackedHvSocketListener(
            new FixedStore(stored), NullLogger<SettingBackedHvSocketListener>.Instance,
            o => { created.Add(o); return new StubListener(); });

        await listener.AcceptAsync("vm", TestContext.Current.CancellationToken);

        Assert.Equal(HvSocketListenerOptions.Default.ServiceId, Assert.Single(created).ServiceId);
    }

    [Fact]
    public async Task The_settings_backed_listener_surfaces_a_store_outage_without_caching_a_default()
    {
        var store = new FixedStore("x") { Fail = true };
        var created = 0;
        await using var listener = new SettingBackedHvSocketListener(
            store, NullLogger<SettingBackedHvSocketListener>.Instance, _ => { created++; return new StubListener(); });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await listener.AcceptAsync("vm", TestContext.Current.CancellationToken));
        Assert.Equal(0, created);
        store.Fail = false;
        store.Value = Guid.NewGuid().ToString("D");
        await listener.AcceptAsync("vm", TestContext.Current.CancellationToken);
        Assert.Equal(1, created);
    }

    private sealed class StubListener : IGuestConnectionListener
    {
        public GuestTransportKind Kind => GuestTransportKind.Loopback;

        public ValueTask<GuestConnection> AcceptAsync(string vmId, CancellationToken ct) =>
            ValueTask.FromResult(new GuestConnection(vmId, GuestTransportKind.Loopback, new MemoryStream()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedStore(string value) : ISettingStore
    {
        public string Value { get; set; } = value;

        public bool Fail { get; set; }

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("store down");
            }

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(Value));
            return Task.FromResult(key.Read(json.RootElement));
        }

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
