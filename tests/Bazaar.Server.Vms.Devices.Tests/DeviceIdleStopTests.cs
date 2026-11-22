using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Vms.Devices.Tests;

/// <summary>A settings store with values a test can change between reads.</summary>
internal sealed class MutableSettingStore : ISettingStore
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public int Reads { get; private set; }

    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Values.TryGetValue(key.Name, out var value) ? (T)value : key.Default);
    }

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>The live device-guest policy and the idle-stop sweep (doc 62 section 9.3).</summary>
public sealed class DeviceIdleStopTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MemoryStore _store = new();
    private readonly Backend _devices = new();
    private readonly Backend _pairBackend = new();
    private readonly MutableSettingStore _settings = new();
    private readonly FakeTimeProvider _clock = new(_epoch);
    private readonly Activity _activity = new();
    private readonly AuditLog _audit = new();

    private VmLifecycleService Lifecycle()
    {
        var router = new VmBackendRouter(
            [new Source("android", VmProviderPlacement.OnDemand, _devices, VmOs.Android), new Source("hyperv", VmProviderPlacement.Pair, _pairBackend, VmOs.Windows, VmOs.Linux)],
            new VmPairSelection("hyperv"),
            gate: null);
        return new VmLifecycleService(_store, router, new VmOperationLocks(), _clock, activity: _activity);
    }

    private DeviceIdleStopSweeper Sweeper(VmLifecycleService? lifecycle = null) =>
        new(lifecycle ?? Lifecycle(), new DeviceGuestSettingsPolicy(_settings), _clock, NullLogger<DeviceIdleStopSweeper>.Instance, _audit);

    private EnvironmentVmRecord Running(VmOs os, string kind, DateTimeOffset startedAt)
    {
        var vm = new EnvironmentVmRecord(
            Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), "ref-" + Guid.NewGuid().ToString("N"), VmState.Running, 2, 2048, 30,
            startedAt, startedAt, kind);
        _store.Rows[vm.Id] = vm;
        return vm;
    }

    [Theory]
    [InlineData("vms.devices.max_concurrent", "0", true)]
    [InlineData("vms.devices.max_concurrent", "64", true)]
    [InlineData("vms.devices.max_concurrent", "65", false)]
    [InlineData("vms.devices.max_concurrent", "-1", false)]
    [InlineData("vms.devices.idle_stop_minutes", "0", true)]
    [InlineData("vms.devices.idle_stop_minutes", "1440", true)]
    [InlineData("vms.devices.idle_stop_minutes", "1441", false)]
    public void Both_live_device_limits_accept_zero(string key, string json, bool valid)
    {
        var setting = DeviceProvidersSettings.All.Single(s => s.Name == key);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(valid, setting.CheckValue(doc.RootElement.Clone()) is null);
    }

    [Fact]
    public async Task The_policy_reads_both_keys_on_every_call()
    {
        var policy = new DeviceGuestSettingsPolicy(_settings);
        Assert.Equal(2, await policy.GetMaxConcurrentAsync(Ct));
        Assert.Equal(10, await policy.GetIdleStopMinutesAsync(Ct));

        _settings.Values["vms.devices.max_concurrent"] = 5;
        _settings.Values["vms.devices.idle_stop_minutes"] = 0;

        Assert.Equal(5, await policy.GetMaxConcurrentAsync(Ct));
        Assert.Equal(0, await policy.GetIdleStopMinutesAsync(Ct));
    }

    [Fact]
    public async Task The_device_cap_is_live_through_the_policy()
    {
        Running(VmOs.Android, "android", _epoch);
        var next = Running(VmOs.Android, "android", _epoch) with { State = VmState.Stopped };
        _store.Rows[next.Id] = next;
        _settings.Values["vms.devices.max_concurrent"] = 1;
        var lifecycle = new VmLifecycleService(
            _store,
            new VmBackendRouter([new Source("android", VmProviderPlacement.OnDemand, _devices, VmOs.Android)], VmPairSelection.None, gate: null),
            new VmOperationLocks(), _clock, devicePolicy: new DeviceGuestSettingsPolicy(_settings));

        Assert.IsType<VmOperationResult.Refused>(await lifecycle.StartAsync(next.Id, Ct));
        _settings.Values["vms.devices.max_concurrent"] = 2;
        Assert.IsType<VmOperationResult.Applied>(await lifecycle.StartAsync(next.Id, Ct));
    }

    [Fact]
    public async Task An_idle_device_guest_is_stopped_and_audited()
    {
        var vm = Running(VmOs.Android, "android", _epoch);
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, await Sweeper().RunOnceAsync(Ct));

        Assert.Equal(VmState.Stopped, _store.Rows[vm.Id].State);
        Assert.Equal(["Stop"], _devices.Calls);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(DeviceIdleStopSweeper.AuditKind, entry.Kind);
        Assert.Equal(DeviceIdleStopSweeper.Actor, entry.Actor);
        using var payload = JsonDocument.Parse(entry.Payload);
        Assert.Equal(vm.Id.ToString(), payload.RootElement.GetProperty("vm_id").GetString());
        Assert.Equal("android", payload.RootElement.GetProperty("provider_kind").GetString());
        Assert.Equal(11, payload.RootElement.GetProperty("idle_minutes").GetInt32());
    }

    [Fact]
    public async Task A_guest_used_recently_is_kept()
    {
        Running(VmOs.Android, "android", _epoch);
        _clock.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(0, await Sweeper().RunOnceAsync(Ct));
        Assert.Empty(_devices.Calls);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task A_busy_guest_is_never_stopped(bool exec, bool terminal, bool browser)
    {
        var vm = Running(VmOs.Android, "android", _epoch);
        _activity.Values[vm.Id] = new VmActivity(exec, terminal, browser, _epoch);
        _clock.Advance(TimeSpan.FromHours(5));

        Assert.Equal(0, await Sweeper().RunOnceAsync(Ct));
        Assert.Equal(VmState.Running, _store.Rows[vm.Id].State);
    }

    [Fact]
    public async Task Last_use_reported_by_the_activity_source_keeps_a_guest_running()
    {
        var vm = Running(VmOs.Android, "android", _epoch);
        _activity.Values[vm.Id] = new VmActivity(false, false, false, _epoch.AddMinutes(30));
        _clock.Advance(TimeSpan.FromMinutes(35));

        Assert.Equal(0, await Sweeper().RunOnceAsync(Ct));
    }

    [Fact]
    public async Task A_pair_vm_is_never_stopped_by_the_sweep()
    {
        var vm = Running(VmOs.Linux, "hyperv", _epoch);
        _clock.Advance(TimeSpan.FromHours(5));

        Assert.Equal(0, await Sweeper().RunOnceAsync(Ct));
        Assert.Equal(VmState.Running, _store.Rows[vm.Id].State);
        Assert.Empty(_pairBackend.Calls);
    }

    [Fact]
    public async Task Zero_disables_the_sweep_and_a_change_applies_on_the_next_pass()
    {
        Running(VmOs.Android, "android", _epoch);
        _clock.Advance(TimeSpan.FromMinutes(20));
        _settings.Values["vms.devices.idle_stop_minutes"] = 0;
        var sweeper = Sweeper();

        Assert.Equal(0, await sweeper.RunOnceAsync(Ct));
        _settings.Values["vms.devices.idle_stop_minutes"] = 30;
        Assert.Equal(0, await sweeper.RunOnceAsync(Ct));
        _settings.Values["vms.devices.idle_stop_minutes"] = 15;
        Assert.Equal(1, await sweeper.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task A_failed_stop_is_skipped_and_the_other_guests_are_still_swept()
    {
        var first = Running(VmOs.Android, "android", _epoch);
        var second = Running(VmOs.Android, "android", _epoch);
        _devices.FailRef = first.BackendRef;
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, await Sweeper().RunOnceAsync(Ct));
        Assert.Equal(VmState.Running, _store.Rows[first.Id].State);
        Assert.Equal(VmState.Stopped, _store.Rows[second.Id].State);
        Assert.Single(_audit.Entries);
    }

    [Fact]
    public void The_component_registers_the_policy_and_the_idle_stop_service()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new DeviceProvidersComponent().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(IDeviceGuestPolicy));
        Assert.Single(services, d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(DeviceIdleStopService));
    }

    private sealed class MemoryStore : IEnvironmentVmStore
    {
        public Dictionary<Guid, EnvironmentVmRecord> Rows { get; } = [];

        public Task<EnvironmentVmRecord?> GetAsync(Guid vmId, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(vmId));

        public Task InsertAsync(EnvironmentVmRecord vm, CancellationToken ct)
        {
            Rows.Add(vm.Id, vm);
            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateStateAsync(Guid vmId, VmState expected, VmState newState, DateTimeOffset now, CancellationToken ct)
        {
            if (!Rows.TryGetValue(vmId, out var row) || row.State != expected)
                return Task.FromResult(false);
            Rows[vmId] = row with { State = newState, UpdatedAt = now };
            return Task.FromResult(true);
        }

        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct) => Task.FromResult(false);

        public Task<IReadOnlyList<EnvironmentVmRecord>> ListNonRetiredAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<EnvironmentVmRecord>>([.. Rows.Values.Where(r => r.State != VmState.Retired).OrderBy(r => r.Id)]);
    }

    private sealed class Backend : IVmBackend
    {
        public List<string> Calls { get; } = [];

        public string? FailRef { get; set; }

        public Task<VmHandle> CreateChildAsync(ImageVersionId parent, VmSpec spec, CancellationToken ct) => throw new NotSupportedException();

        public Task StartAsync(VmHandle vm, CancellationToken ct) => Record("Start", vm);

        public Task StopAsync(VmHandle vm, StopMode mode, CancellationToken ct) => Record("Stop", vm);

        public Task SaveStateAsync(VmHandle vm, CancellationToken ct) => Record("Save", vm);

        public Task<CheckpointId> CheckpointAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        public Task RestoreAsync(VmHandle vm, CheckpointId id, CancellationToken ct) => throw new NotSupportedException();

        public Task RetireAsync(VmHandle vm, CancellationToken ct) => Record("Retire", vm);

        public Task<VmState> GetStateAsync(VmHandle vm, CancellationToken ct) => throw new NotSupportedException();

        private Task Record(string operation, VmHandle vm)
        {
            if (vm.BackendRef == FailRef)
                throw new InvalidOperationException("backend failure");
            Calls.Add(operation);
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

    private sealed class Activity : IVmActivitySource
    {
        public Dictionary<Guid, VmActivity> Values { get; } = [];

        public ValueTask<VmActivity?> GetAsync(Guid vmId, CancellationToken ct) => ValueTask.FromResult(Values.TryGetValue(vmId, out var a) ? a : null);
    }

    private sealed class AuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }
}
