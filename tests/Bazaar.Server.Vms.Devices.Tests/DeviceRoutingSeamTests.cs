using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vms.Devices.Tests;

/// <summary>The devices module's part of doc 62 section 9.2: the enabled gate, the live keys and the listing's refresh.</summary>
public sealed class DeviceRoutingSeamTests
{
    private static readonly JsonWireCodec _codec = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_gate_answers_from_vms_devices_enabled()
    {
        var gate = new DeviceProvidersGate(new FakeSettingStore("android"));

        Assert.True(await gate.IsEnabledAsync("android", Ct));
        Assert.False(await gate.IsEnabledAsync("microvm", Ct));
        Assert.False(await gate.IsEnabledAsync("ANDROID", Ct));
    }

    [Fact]
    public void The_component_registers_the_gate()
    {
        var services = new ServiceCollection();
        new DeviceProvidersComponent().AddServices(services, new ConfigurationBuilder().Build());
        services.AddSingleton<ISettingStore>(new FakeSettingStore());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DeviceProvidersGate>(provider.GetRequiredService<IVmProviderGate>());
    }

    [Fact]
    public void The_live_keys_are_the_ones_doc_62_section_7_marks_live_and_all_are_registered()
    {
        Assert.Equal(
            ["vms.android.max_concurrent", "vms.devices.idle_stop_minutes", "vms.devices.max_concurrent", "vms.microvm.max_concurrent"],
            DeviceProvidersSettings.LiveKeys.Order(StringComparer.Ordinal));
        Assert.All(DeviceProvidersSettings.LiveKeys, k => Assert.Contains(DeviceProvidersSettings.All, d => d.Name == k));
        Assert.True(DeviceProvidersSettings.AppliesLive(DeviceProvidersSettings.AndroidMaxConcurrent));
        Assert.False(DeviceProvidersSettings.AppliesLive(DeviceProvidersSettings.AndroidSdkRoot));
        Assert.False(DeviceProvidersSettings.AppliesLive(DeviceProvidersSettings.Enabled));
    }

    [Fact]
    public async Task The_listing_refreshes_each_enabled_entry_before_it_reads_it_and_survives_a_failed_refresh()
    {
        var refreshed = new RefreshingEntry("android");
        var failing = new RefreshingEntry("microvm") { Fail = true };
        var skipped = new RefreshingEntry("ios");
        var handler = new DeviceListWireHandler(new FakeSettingStore("android", "microvm"), [refreshed, failing, skipped]);

        var reply = await handler.HandleAsync(Request(), Ct);

        Assert.True(reply.IsOk);
        var list = _codec.DecodePayload<DeviceListReply>(new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Response, "r", "r", null, null, null, reply.Payload));
        Assert.Equal(["android", "microvm"], list.Providers.Select(p => p.Kind));
        Assert.True(Assert.Single(list.Providers, p => p.Kind == "android").Usable);
        Assert.Equal(1, refreshed.Refreshes);
        Assert.Equal(0, skipped.Refreshes);
    }

    private static WireRequestContext Request() =>
        new(new Connection(),
            new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Request, "r", null, null, null, null, _codec.EncodePayload(new DeviceListRequest())));

    private sealed class RefreshingEntry(string kind) : IDeviceProviderCatalogEntry
    {
        public bool Fail { get; init; }

        public int Refreshes { get; private set; }

        public string Kind => kind;

        public DeviceProviderCapabilities Capabilities { get; } = new(
            "android", DeviceIsolation.HardwareVirtualised, Save: false, Checkpoint: false, Restore: false, Headless: true,
            MaxConcurrentInstances: 1, GuestAgent: false, EgressEnforced: false, ParentContentPinned: true, ScreenSource: false, RemoteNode: false);

        public int Running => 0;

        // Usable only once refreshed, so the reply shows the refresh ran first.
        public DeviceProbeResult ProbeDevice() => new(Refreshes > 0, Refreshes > 0 ? null : "not loaded");

        public IReadOnlyList<DeviceGuestState> RunningGuests() => [];

        public ValueTask RefreshAsync(CancellationToken ct)
        {
            if (Fail)
                throw new InvalidOperationException("store down");
            Refreshes++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = new(Guid.CreateVersion7(), "owner");
        public string DeviceId => "test";
        public string DeviceClass => "test";
        public IWireCodec Codec => _codec;
        public IReadOnlyList<StreamCursor> HelloCursors => [];
        public Server.Wire.Sync.SubscriptionSession? Subscriptions => null;
        public CancellationToken Closed => CancellationToken.None;
        public bool TryEnqueueEvent(Envelope envelope) => true;
        public bool TryEnqueueLagged(Lagged lagged) => true;
        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
