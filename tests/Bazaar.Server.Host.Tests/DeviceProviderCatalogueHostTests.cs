using System.Text.Json;
using Bazaar.Contracts.Vms;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Android;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.MicroVm;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The Android and microVM providers are part of the host and reach <c>vms.devices.list</c> (doc 62 section 9.1).</summary>
public sealed class DeviceProviderCatalogueHostTests
{
    private static readonly JsonWireCodec _codec = new();

    private sealed class Store(params string[] enabled) : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key.Name == DeviceProvidersSettings.Enabled.Name ? (T)(object)(IReadOnlyList<string>)enabled : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

    private static async Task<DeviceListReply> List(params string[] enabled)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        new DeviceProvidersComponent().AddServices(services, config);
        new AndroidComponent().AddServices(services, config);
        new MicroVmComponent().AddServices(services, config);
        services.AddSingleton<ISettingStore>(new Store(enabled));
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetServices<IWireRequestHandler>().Single(h => h.Type == WireTypes.DeviceList);
        var request = new WireRequestContext(
            new Connection(),
            new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Request, "req-1", null, null, null, null, _codec.EncodePayload(new DeviceListRequest())));

        var reply = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.True(reply.IsOk);
        return _codec.DecodePayload<DeviceListReply>(new Envelope(1, WireTypes.DeviceList, EnvelopeKind.Response, "req-1", "req-1", null, null, null, reply.Payload));
    }

    [Fact]
    public void The_host_ships_both_components_after_settings()
    {
        Assert.Contains(HostComponents.All, c => c is AndroidComponent);
        Assert.Contains(HostComponents.All, c => c is MicroVmComponent);

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.True(order.IndexOf("settings") < order.IndexOf("vms-android"));
        Assert.True(order.IndexOf("settings") < order.IndexOf("vms-microvm"));
    }

    [Fact]
    public async Task Android_enabled_with_an_unset_sdk_root_is_listed_as_not_usable_with_a_reason()
    {
        var reply = await List("android");

        var android = Assert.Single(reply.Providers);
        Assert.Equal("android", android.Kind);
        Assert.False(android.Usable);
        Assert.Contains("vms.android.sdk_root", android.Reason, StringComparison.Ordinal);
        Assert.Empty(reply.Guests);
    }

    [Fact]
    public async Task Both_enabled_are_listed_and_nothing_enabled_lists_nothing()
    {
        var both = await List("android", "microvm");
        var none = await List();

        Assert.Equal(["android", "microvm"], both.Providers.Select(p => p.Kind).Order().ToArray());
        Assert.All(both.Providers, p => Assert.False(p.Usable));
        Assert.Empty(none.Providers);
    }
}
