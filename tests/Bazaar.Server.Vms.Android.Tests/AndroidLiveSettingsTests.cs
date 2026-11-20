using System.Text.Json;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Vms.Android.Tests;

/// <summary>The Android entry as a backend source with live settings (doc 62 section 9.2).</summary>
public sealed class AndroidLiveSettingsTests
{
    private const string _image = "system-images;android-35;google_apis;x86_64";
    private static readonly DateTimeOffset _epoch = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly VmSpec _spec = new(VmOs.Android, 2, 4096, 16);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Root => OperatingSystem.IsWindows() ? @"V:\android" : "/opt/android";

    private readonly FakeAndroidHost _host = new();
    private readonly FakeTimeProvider _clock = new(_epoch);
    private readonly Dictionary<string, object> _values = [];
    private int _builds;

    private static Guid ImageId => AndroidCatalogEntry.ImageVersion(new ConfigurationBuilder().Build(), _image);

    private void Configure(int max = 1)
    {
        _values[DeviceProvidersSettings.AndroidSdkRoot.Name] = Root + "/sdk";
        _values[DeviceProvidersSettings.AndroidAvdHome.Name] = Root + "/avd";
        _values[DeviceProvidersSettings.AndroidSystemImage.Name] = _image;
        _values[DeviceProvidersSettings.AndroidMaxConcurrent.Name] = max;
    }

    private AndroidCatalogEntry Entry(ISettingStore? store = null) => new(
        store ?? new MapSettingStore(_values),
        new ConfigurationBuilder().Build(),
        _host,
        (o, w) =>
        {
            _builds++;
            return new AndroidBackend(_host, _host, w, o);
        },
        _clock);

    private void Advance() => _clock.Advance(DeviceProviderEntry<AndroidBackend>.DefaultRefreshInterval);

    [Fact]
    public async Task A_live_cap_change_reaches_the_same_backend_and_affects_only_new_starts()
    {
        Configure(max: 1);
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend!;
        var first = await backend.CreateChildAsync(new(ImageId), _spec, Ct);
        var second = await backend.CreateChildAsync(new(ImageId), _spec, Ct);
        await backend.StartAsync(first, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartAsync(second, Ct));
        var generation = entry.Generation;

        _values[DeviceProvidersSettings.AndroidMaxConcurrent.Name] = 2;
        Advance();
        await entry.RefreshAsync(Ct);

        Assert.Same(backend, entry.Backend);
        Assert.Equal(1, _builds);
        Assert.Equal(2, entry.Capabilities.MaxConcurrentInstances);
        Assert.True(entry.Generation > generation);
        await backend.StartAsync(second, Ct);
        Assert.Equal(2, entry.Running);
    }

    [Fact]
    public async Task Lowering_the_cap_below_the_running_count_stops_nothing_and_refuses_further_starts()
    {
        Configure(max: 2);
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend!;
        var vms = new List<VmHandle>();
        for (var i = 0; i < 3; i++)
            vms.Add(await backend.CreateChildAsync(new(ImageId), _spec, Ct));
        await backend.StartAsync(vms[0], Ct);
        await backend.StartAsync(vms[1], Ct);

        _values[DeviceProvidersSettings.AndroidMaxConcurrent.Name] = 1;
        Advance();
        await entry.RefreshAsync(Ct);

        Assert.Equal(2, _host.Running.Count);
        await backend.StopAsync(vms[0], StopMode.Graceful, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartAsync(vms[2], Ct));
    }

    [Fact]
    public async Task An_install_key_change_does_not_rebuild_a_built_backend()
    {
        Configure();
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        var backend = entry.Backend;

        _values[DeviceProvidersSettings.AndroidSdkRoot.Name] = Root + "/other-sdk";
        Advance();
        await entry.RefreshAsync(Ct);

        Assert.Same(backend, entry.Backend);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task An_unconfigured_entry_becomes_usable_after_the_interval_once_the_settings_are_complete()
    {
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        Assert.Null(entry.Backend);
        Assert.Contains("vms.android.sdk_root", entry.ProbeDevice().Reason, StringComparison.Ordinal);

        Configure();
        await entry.RefreshAsync(Ct);
        Assert.Null(entry.Backend);

        Advance();
        await entry.RefreshAsync(Ct);
        Assert.NotNull(entry.Backend);
        Assert.True(entry.ProbeDevice().Usable);
    }

    [Fact]
    public async Task Invalidate_makes_the_next_refresh_read_at_once()
    {
        using var entry = Entry();
        await entry.RefreshAsync(Ct);
        Configure();

        entry.Invalidate();
        await entry.RefreshAsync(Ct);

        Assert.NotNull(entry.Backend);
    }

    [Fact]
    public void Before_the_first_refresh_the_entry_reports_not_usable_without_reading_the_store()
    {
        var store = new CountingStore();
        using var entry = Entry(store);

        Assert.False(entry.ProbeDevice().Usable);
        Assert.False(entry.Probe().Usable);
        Assert.Equal(0, store.Reads);
        Assert.Equal("android", entry.Capabilities.GuestOs);
    }

    [Fact]
    public async Task A_store_that_fails_is_reported_with_a_fixed_reason_and_read_again_on_the_next_refresh()
    {
        var store = new CountingStore { Fail = true };
        using var entry = Entry(store);

        await entry.RefreshAsync(Ct);
        Assert.Equal("The Android settings could not be read.", entry.ProbeDevice().Reason);
        var reads = store.Reads;

        await entry.RefreshAsync(Ct);
        Assert.True(store.Reads > reads);
    }

    [Fact]
    public async Task As_a_backend_source_it_is_on_demand_for_android_and_hands_out_its_backend()
    {
        Configure();
        using var entry = Entry();

        var lookup = await entry.GetCurrentAsync(Ct);

        Assert.Equal(VmProviderPlacement.OnDemand, entry.Placement);
        Assert.Equal([VmOs.Android], entry.GuestOs);
        Assert.True(lookup.Usable);
        Assert.Same(entry.Backend, lookup.Backend);

        _host.Facts = new("linux", "x64", new HashSet<string>());
        var unusable = await entry.GetCurrentAsync(Ct);
        Assert.False(unusable.Usable);
        Assert.NotNull(unusable.Backend);
    }

    [Fact]
    public async Task The_lifecycle_creates_and_starts_an_android_guest_through_the_router()
    {
        Configure(max: 2);
        using var entry = Entry();
        var store = new MemoryVmStore();
        var router = new VmBackendRouter([entry], VmPairSelection.None, new DeviceProvidersGate(new EnabledStore("android")));
        var lifecycle = new VmLifecycleService(store, router, new VmOperationLocks(), _clock);
        var vm = new EnvironmentVmRecord(Guid.CreateVersion7(), Guid.CreateVersion7(), VmOs.Android, ImageId, null, VmState.Absent, 2, 4096, 16, _epoch, _epoch);

        Assert.IsType<VmOperationResult.Applied>(await lifecycle.CreateAsync(vm, Ct));
        Assert.IsType<VmOperationResult.Applied>(await lifecycle.StartAsync(vm.Id, Ct));

        Assert.Equal("android", store.Rows[vm.Id].ProviderKind);
        Assert.Equal(VmState.Running, store.Rows[vm.Id].State);
        Assert.Single(_host.Running);
        Assert.Equal(1, entry.Running);
    }

    [Fact]
    public void The_component_registers_the_entry_as_a_backend_source()
    {
        var services = new ServiceCollection();
        new AndroidComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new MapSettingStore([]));
        using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<AndroidCatalogEntry>(), Assert.Single(provider.GetServices<IVmBackendSource>()));
    }

    private sealed class CountingStore : ISettingStore
    {
        public int Reads { get; private set; }

        public bool Fail { get; init; }

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default)
        {
            Reads++;
            return Fail ? throw new InvalidOperationException("store down") : Task.FromResult(key.Default);
        }

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EnabledStore(params string[] enabled) : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key.Name == DeviceProvidersSettings.Enabled.Name ? (T)(object)(IReadOnlyList<string>)enabled : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryVmStore : IEnvironmentVmStore
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

        public Task<bool> SetBackendRefAsync(Guid vmId, string backendRef, DateTimeOffset now, CancellationToken ct)
        {
            if (!Rows.TryGetValue(vmId, out var row))
                return Task.FromResult(false);
            Rows[vmId] = row with { BackendRef = backendRef, UpdatedAt = now };
            return Task.FromResult(true);
        }
    }
}
