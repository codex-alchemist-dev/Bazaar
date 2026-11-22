using System.Text.Json;
using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Vms.Devices.Tests;

internal sealed class FakeSettingStore(params string[] enabled) : ISettingStore
{
    public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
        Task.FromResult(key.Name == DeviceProvidersSettings.Enabled.Name ? (T)(object)(IReadOnlyList<string>)enabled : key.Default);

    public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeEntry(
    string kind,
    string guestOs,
    DeviceProbeResult? probe = null,
    IReadOnlyList<DeviceGuestState>? guests = null,
    int max = 2,
    bool probeThrows = false,
    IReadOnlyList<string>? restartRequired = null) : IDeviceProviderCatalogEntry
{
    public string Kind => kind;

    public IReadOnlyList<string> RestartRequiredKeys => restartRequired ?? [];

    public DeviceProviderCapabilities Capabilities { get; } = new(
        guestOs, DeviceIsolation.HardwareVirtualised, Save: true, Checkpoint: false, Restore: true, Headless: true,
        MaxConcurrentInstances: max, GuestAgent: false, EgressEnforced: false, ParentContentPinned: true,
        ScreenSource: true, RemoteNode: false);

    public int Running => RunningGuests().Count;

    public DeviceProbeResult ProbeDevice() =>
        probeThrows ? throw new InvalidOperationException("secret host path /opt/sdk") : probe ?? new DeviceProbeResult(true, null);

    public IReadOnlyList<DeviceGuestState> RunningGuests() => guests ?? [];
}

public sealed class DeviceListWireHandlerTests
{
    private static readonly JsonWireCodec _codec = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeConnection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.CreateVersion7();
        public PrincipalRef Principal { get; } = principal;
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

    private static WireRequestContext Request(string principalKind) =>
        new(new FakeConnection(new PrincipalRef(Guid.CreateVersion7(), principalKind)),
            new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Request, "req-1", null, null, null, null,
                _codec.EncodePayload(new DeviceListRequest())));

    private static DeviceListReply Decode(WireReply reply)
    {
        Assert.True(reply.IsOk);
        return _codec.DecodePayload<DeviceListReply>(new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, reply.Payload));
    }

    private static DeviceListWireHandler Make(string[] enabled, params IDeviceProviderCatalogEntry[] entries) =>
        new(new FakeSettingStore(enabled), entries);

    [Fact]
    public async Task Restart_required_keys_are_listed_per_provider_and_null_when_none()
    {
        var handler = Make(
            ["android", "microvm"],
            new FakeEntry("android", "android", restartRequired: ["vms.android.sdk_root"]),
            new FakeEntry("microvm", "linux"));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        Assert.Equal(["vms.android.sdk_root"], reply.Providers[0].RestartRequired);
        Assert.Null(reply.Providers[1].RestartRequired);
    }

    [Fact]
    public void An_older_reply_without_the_field_still_parses()
    {
        var old = """{"providers":[{"kind":"android","guestOs":"android","usable":true,"reason":null,"isolation":"x","headless":true,"save":false,"checkpoint":false,"restore":false,"egressEnforced":false,"maxConcurrentInstances":null,"running":0}],"guests":[]}"""u8.ToArray();

        var reply = _codec.DecodePayload<DeviceListReply>(new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Response,
            "req-1", "req-1", null, null, null, old));

        Assert.Null(Assert.Single(reply.Providers).RestartRequired);
    }

    [Fact]
    public void The_list_is_served_on_its_own_wire_type()
    {
        Assert.Equal(WireTypes.DeviceList, Make([]).Type);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("member")]
    [InlineData("service")]
    public async Task A_non_owner_gets_not_found_and_nothing_else(string kind)
    {
        var handler = Make(["android"], new FakeEntry("android", "android"));

        var reply = await handler.HandleAsync(Request(kind), Ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public async Task With_nothing_enabled_the_owner_gets_two_empty_lists()
    {
        var handler = Make([], new FakeEntry("android", "android", guests: [new(Guid.CreateVersion7(), "Running")]));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        Assert.Empty(reply.Providers);
        Assert.Empty(reply.Guests);
    }

    [Fact]
    public async Task Only_enabled_providers_and_their_running_guests_appear()
    {
        var androidGuest = Guid.CreateVersion7();
        var microGuest = Guid.CreateVersion7();
        var handler = Make(
            ["android"],
            new FakeEntry("android", "android", guests: [new(androidGuest, "Running")], max: 3),
            new FakeEntry("microvm", "linux", guests: [new(microGuest, "Running")]));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        var provider = Assert.Single(reply.Providers);
        Assert.Equal("android", provider.Kind);
        Assert.Equal("android", provider.GuestOs);
        Assert.True(provider.Usable);
        Assert.Null(provider.Reason);
        Assert.Equal("HardwareVirtualised", provider.Isolation);
        Assert.True(provider.Headless);
        Assert.True(provider.Save);
        Assert.False(provider.Checkpoint);
        Assert.True(provider.Restore);
        Assert.False(provider.EgressEnforced);
        Assert.Equal(3, provider.MaxConcurrentInstances);
        Assert.Equal(1, provider.Running);
        var guest = Assert.Single(reply.Guests);
        Assert.Equal(new DeviceGuestInfo(androidGuest, "android", "android", "Running"), guest);
    }

    [Fact]
    public async Task Enabled_order_is_kept_and_a_kind_without_an_installed_module_is_left_out()
    {
        var handler = Make(
            ["microvm", "ios", "android"],
            new FakeEntry("android", "android"),
            new FakeEntry("microvm", "linux"));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        Assert.Equal(["microvm", "android"], reply.Providers.Select(p => p.Kind).ToArray());
    }

    [Fact]
    public async Task An_unusable_provider_shows_its_reason_and_a_throwing_probe_shows_fixed_text()
    {
        var handler = Make(
            ["android", "microvm"],
            new FakeEntry("android", "android", probe: new DeviceProbeResult(false, "The emulator is not installed.")),
            new FakeEntry("microvm", "linux", probeThrows: true));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        var android = reply.Providers.Single(p => p.Kind == "android");
        Assert.False(android.Usable);
        Assert.Equal("The emulator is not installed.", android.Reason);
        var micro = reply.Providers.Single(p => p.Kind == "microvm");
        Assert.False(micro.Usable);
        Assert.DoesNotContain("/opt/sdk", micro.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_cap_is_reported_as_no_cap()
    {
        var handler = Make(["android"], new FakeEntry("android", "android", max: 0));

        var reply = Decode(await handler.HandleAsync(Request("owner"), Ct));

        Assert.Null(Assert.Single(reply.Providers).MaxConcurrentInstances);
    }

    [Fact]
    public void Constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceListWireHandler(null!, []));
        Assert.Throws<ArgumentNullException>(() => new DeviceListWireHandler(new FakeSettingStore(), null!));
    }
}
