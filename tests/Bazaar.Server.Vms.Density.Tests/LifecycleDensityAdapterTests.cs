using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Vms.Density.Tests;

/// <summary>The lifecycle adapters for the density governor and their registration (doc 62 section 9.3).</summary>
public sealed class LifecycleDensityAdapterTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MemoryStore _store = new();
    private readonly Backend _android = new() { SupportsSaveState = true };
    private readonly Backend _microVm = new();
    private readonly Backend _pair = new();

    private VmBackendRouter Router() => new(
        [
            new Source("android", VmProviderPlacement.OnDemand, _android, VmOs.Android),
            new Source("microvm", VmProviderPlacement.OnDemand, _microVm, VmOs.Linux),
            new Source("hyperv", VmProviderPlacement.Pair, _pair, VmOs.Windows, VmOs.Linux),
        ],
        new VmPairSelection("hyperv"),
        gate: null);

    private VmLifecycleService Lifecycle(IDensityGovernor? governor = null) =>
        new(_store, Router(), new VmOperationLocks(), TimeProvider.System, governor);

    private EnvironmentVmRecord Seed(VmState state, VmOs os, string kind, int memoryMb = 2048, DateTimeOffset? at = null)
    {
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), "ref-" + Guid.NewGuid().ToString("N"), state, 2, memoryMb, 30,
            at ?? _epoch, at ?? _epoch, kind);
        _store.Rows[vm.Id] = vm;
        return vm;
    }

    [Fact]
    public async Task The_directory_lists_running_vms_with_their_own_backend_save_support()
    {
        var android = Seed(VmState.Running, VmOs.Android, "android", 3072);
        var microVm = Seed(VmState.Running, VmOs.Linux, "microvm", 512);
        var pair = Seed(VmState.Running, VmOs.Windows, "hyperv");
        Seed(VmState.Stopped, VmOs.Android, "android");
        var lifecycle = Lifecycle();

        var running = await new LifecycleDensityVmDirectory(() => lifecycle).ListRunningAsync(Ct);

        Assert.Equal(3, running.Count);
        var a = Assert.Single(running, r => r.Vm.VmId == android.Id);
        Assert.Equal(new DensityVm(android.Id, android.AgentId.ToString(), DensityGuestOs.Android, 3072), a.Vm);
        Assert.True(a.CanSaveState);
        Assert.Equal(new VmActivity(false, false, false, _epoch), a.Activity);
        var m = Assert.Single(running, r => r.Vm.VmId == microVm.Id);
        Assert.Equal(DensityGuestOs.Linux, m.Vm.Os);
        Assert.False(m.CanSaveState);
        Assert.Equal(DensityGuestOs.Windows, Assert.Single(running, r => r.Vm.VmId == pair.Id).Vm.Os);
    }

    [Fact]
    public async Task The_control_saves_through_the_vms_own_backend()
    {
        var vm = Seed(VmState.Running, VmOs.Android, "android");
        var lifecycle = Lifecycle();

        await new LifecycleDensityVmControl(() => lifecycle).SaveAsync(vm.Id, Ct);

        Assert.Equal(VmState.Saved, _store.Rows[vm.Id].State);
        Assert.Equal(["Save"], _android.Calls);
        Assert.Empty(_pair.Calls);
    }

    [Fact]
    public async Task A_refused_save_is_thrown_so_the_governor_skips_the_vm()
    {
        var vm = Seed(VmState.Stopped, VmOs.Android, "android");
        var lifecycle = Lifecycle();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new LifecycleDensityVmControl(() => lifecycle).SaveAsync(vm.Id, Ct));

        Assert.Contains("ForbiddenTransition", error.Message, StringComparison.Ordinal);
        Assert.Empty(_android.Calls);
    }

    [Fact]
    public void Nothing_is_registered_without_a_governor()
    {
        var services = new ServiceCollection();

        Assert.False(services.AddVmDensityLifecycleAdapters());
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDensityVmDirectory) || d.ServiceType == typeof(IDensityVmControl));
    }

    [Fact]
    public async Task With_the_governor_component_the_adapters_activate_it_without_a_construction_cycle()
    {
        var services = new ServiceCollection();
        new VmDensityComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<IHostMemoryProbe>(new StoreMemoryProbe(_store, 32_768));
        services.AddSingleton(sp => new VmLifecycleService(
            _store, Router(), new VmOperationLocks(), TimeProvider.System, sp.GetService<IDensityGovernor>()));

        Assert.True(services.AddVmDensityLifecycleAdapters());
        await using var provider = services.BuildServiceProvider();
        var governor = Assert.IsType<DensityGovernor>(provider.GetRequiredService<IDensityGovernor>());
        Assert.True(governor.IsActive);

        var vm = Seed(VmState.Stopped, VmOs.Android, "android");
        Assert.IsType<VmOperationResult.Applied>(await provider.GetRequiredService<VmLifecycleService>().StartAsync(vm.Id, Ct));
    }

    [Fact]
    public async Task The_governor_saves_an_idle_android_guest_through_the_lifecycle_to_admit_a_start()
    {
        var idle = Seed(VmState.Running, VmOs.Android, "android", 4096, _epoch.AddHours(-1));
        var request = Seed(VmState.Stopped, VmOs.Windows, "hyperv", 4096);
        var options = Options.Create(new VmDensityOptions
        {
            HostReserveMb = 1024,
            MinIdleBeforeSave = TimeSpan.FromMinutes(1),
            SaveSettleDelay = TimeSpan.FromMilliseconds(5),
            RecheckInterval = TimeSpan.FromMilliseconds(20),
            QueueTimeout = TimeSpan.FromSeconds(30),
        });
        VmLifecycleService? lifecycle = null;
        await using var governor = new DensityGovernor(
            options,
            TimeProvider.System,
            new StoreMemoryProbe(_store, 9000), // 4096 running and 1024 reserved leave 3880, short of the 4096 requested
            new LifecycleDensityVmDirectory(() => lifecycle!),
            new LifecycleDensityVmControl(() => lifecycle!));
        lifecycle = Lifecycle(governor);

        var result = await lifecycle.StartAsync(request.Id, Ct);

        Assert.IsType<VmOperationResult.Applied>(result);
        Assert.Equal(VmState.Saved, _store.Rows[idle.Id].State);
        Assert.Equal(VmState.Running, _store.Rows[request.Id].State);
        Assert.Equal(["Save"], _android.Calls);
        Assert.Equal(["Start"], _pair.Calls);
    }

    /// <summary>Free memory is the total minus the memory of every VM whose row is Running.</summary>
    private sealed class StoreMemoryProbe(MemoryStore store, long totalMb) : IHostMemoryProbe
    {
        public Task<HostMemory> ReadAsync(CancellationToken cancellationToken)
        {
            long used;
            lock (store.Rows)
                used = store.Rows.Values.Where(r => r.State == VmState.Running).Sum(r => (long)r.MemoryMb);
            return Task.FromResult(new HostMemory(totalMb, totalMb - used));
        }
    }

    private sealed class MemoryStore : IEnvironmentVmStore
    {
        public Dictionary<Guid, EnvironmentVmRecord> Rows { get; } = [];

        public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct)
        {
            lock (Rows)
                return Task.FromResult(Rows.GetValueOrDefault(vmId));
        }

        public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct)
        {
            lock (Rows)
                Rows.Add(vm.Id, vm);
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct)
        {
            lock (Rows)
            {
                if (!Rows.TryGetValue(vmId, out var row) || row.State != expected)
                    return Task.FromResult(false);
                Rows[vmId] = row with { State = newState, UpdatedAt = now };
                return Task.FromResult(true);
            }
        }

        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredAsync(CancellationToken ct)
        {
            lock (Rows)
                return Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>([.. Rows.Values.Where(r => r.State != VmState.Retired).OrderBy(r => r.Id)]);
        }
    }

    private sealed class Backend : IVmBackend
    {
        private readonly Lock _sync = new();
        private readonly List<string> _calls = [];

        public bool SupportsSaveState { get; init; }

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_sync)
                    return [.. _calls];
            }
        }

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct) => Record("Start");

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => Record("Stop");

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => Record("Save");

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();

        public Task RetireAsync(VmHandle vm, CancellationToken ct) => Record("Retire");

        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        private Task Record(string operation)
        {
            lock (_sync)
                _calls.Add(operation);
            return Task.CompletedTask;
        }
    }

    private sealed class Source(string kind, VmProviderPlacement placement, IVmBackend backend, params VmOs[] os) : IVmBackendSource
    {
        public string Kind => kind;

        public IReadOnlyList<VmOs> GuestOs => os;

        public VmProviderPlacement Placement => placement;

        public int Preference => VmBackendOrdering.OnDemand;

        public ValueTask<VmBackendLookup> GetCurrentAsync(CancellationToken ct) => ValueTask.FromResult(VmBackendLookup.Ready(backend));
    }
}
