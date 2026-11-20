using System.Text.Json;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Android;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MicroVm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host's part of doc 62 section 9.2: providers come from DI, the pair selector orders them, the router sees them all.</summary>
public sealed class VmRoutingCompositionTests
{
    private sealed class Pair(string kind, int preference, int max = 8, bool usable = true) : IVmBackendSource, IVmProvider, ICapacityLimitedProvider
    {
        public string Kind => kind;
        public IReadOnlyList<VmOs> GuestOs { get; } = [VmOs.Windows, VmOs.Linux];
        public VmProviderPlacement Placement => VmProviderPlacement.Pair;
        public int Preference => preference;
        public int MaxConcurrentInstances => max;
        public ProviderStatus Probe() => new(kind, usable, [], usable ? null : "probe failed");
        public ValueTask<VmBackendLookup> GetCurrentAsync(CancellationToken ct) => ValueTask.FromResult(VmBackendLookup.Unavailable("test"));
    }

    private sealed class Store : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) => Task.FromResult(key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Pair_candidates_are_the_pair_sources_in_preference_order_and_never_on_demand_ones()
    {
        var sandbox = new Pair("windows_sandbox", VmBackendOrdering.LastResort, max: 1);
        var hyperV = new Pair("hyperv", VmBackendOrdering.PrimaryHypervisor);
        var android = new AndroidCatalogEntry(new Store(), new ConfigurationBuilder().Build(), new UnavailableAndroidWorkspace());

        var candidates = VmsCompositionExtensions.PairCandidates([sandbox, android, hyperV]);

        Assert.Equal(["hyperv", "windows_sandbox"], candidates.Select(c => c.Kind));
        var selection = ProviderSelector.Select(ProviderRole.Vm, ProviderSelector.Auto, candidates, ProviderRequirements.AgentPairConcurrency);
        Assert.Equal("hyperv", selection.Provider?.Kind);
        Assert.Equal("hyperv", VmsCompositionExtensions.PairSelection(new ProviderCapabilityReport([selection])).Kind);
    }

    [Fact]
    public void A_report_without_a_vm_selection_gives_no_pair_kind()
    {
        Assert.Null(VmsCompositionExtensions.PairSelection(null).Kind);
        Assert.Null(VmsCompositionExtensions.PairSelection(new ProviderCapabilityReport([])).Kind);
    }

    [Fact]
    public async Task The_router_sees_every_provider_module_and_the_lifecycle_resolves_without_a_configured_backend()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddBazaarVms(configuration);
        new DeviceProvidersComponent().AddServices(services, configuration);
        new AndroidComponent().AddServices(services, configuration);
        new MicroVmComponent().AddServices(services, configuration);
        services.AddSingleton<ISettingStore>(new Store());
        // The store is stubbed: the lifecycle needs the repository only to run operations, not to be built.
        services.AddSingleton<IEnvironmentVmStore>(new NoStore());
        await using var provider = services.BuildServiceProvider();

        var router = Assert.IsType<VmBackendRouter>(provider.GetRequiredService<IVmBackendRouter>());

        Assert.Equal(["android", "microvm"], router.Sources.Select(s => s.Kind));
        Assert.NotNull(provider.GetRequiredService<VmLifecycleService>());
        var refused = await router.RouteNewAsync(VmOs.Android, null, TestContext.Current.CancellationToken);
        Assert.Contains("vms.devices.enabled", refused.Reason, StringComparison.Ordinal);
    }

    private sealed class NoStore : IEnvironmentVmStore
    {
        public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) => Task.FromResult<EnvironmentVmRecord?>(null);
        public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);
    }
}
