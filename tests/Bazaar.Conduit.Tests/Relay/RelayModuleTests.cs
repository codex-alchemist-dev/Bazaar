using Bazaar.Conduit.Relay;
using Bazaar.Conduit.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.Relay;

public sealed class RelayModuleTests
{
    private const string _url = "wss://relay.example.test/";
    private const string _room = "room-1";

    private static Dictionary<string, object> Settings(string? url = _url, string? room = _room)
    {
        var values = new Dictionary<string, object>();
        if (url is not null)
        {
            values[RelayModule.UrlKey] = url;
        }

        if (room is not null)
        {
            values[RelayModule.RoomKey] = room;
        }

        return values;
    }

    [Fact]
    public void Identity_matches_the_doc_catalogue()
    {
        var module = new RelayModule(() => new FakeRelaySocket());

        Assert.Equal("relay", module.Id);
        Assert.Equal(ConduitScope.Relay, module.Scope);
        Assert.Equal(50, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Missing_url_fails_not_configured()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings(url: null)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Missing_room_fails_not_configured()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings(room: null)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("not-configured", module.Status.Reason);
        Assert.Empty(factory.Created);
    }

    [Theory]
    [InlineData("ws://relay.example.test/")]
    [InlineData("https://relay.example.test/")]
    [InlineData("relay.example.test")]
    [InlineData("wss://user:secret@relay.example.test/")]
    [InlineData("wss://relay.example.test/?token=abc")]
    public async Task Non_wss_or_credentialed_url_fails_invalid_relay_url(string url)
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings(url: url)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("invalid-relay-url", module.Status.Reason);
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task Valid_settings_publish_one_relay_offer_without_secrets()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Running, module.Status.State);
        Assert.Null(module.Status.Reason);
        var offer = Assert.Single(module.Status.Offers);
        Assert.Equal("relay", offer.ModuleId);
        Assert.Equal(ConduitScope.Relay, offer.Scope);
        Assert.Equal(new Uri("wss://relay.example.test/rooms/room-1"), offer.Uri);
        Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
        Assert.Null(offer.ExpiresAt);
        Assert.True(offer.RequiresRelay);
        Assert.False(offer.Verified);
        Assert.False(offer.TerminatesTls);
        Assert.Equal(string.Empty, offer.Uri.Query);
        Assert.Equal(string.Empty, offer.Uri.UserInfo);
        Assert.Null(ConduitOfferRules.Validate(offer));
        Assert.Equal(new Uri("wss://relay.example.test/"), factory.Latest().ConnectedTo);

        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Registration_frame_is_sent_once_with_the_room()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);

        var frame = Assert.Single(factory.Latest().Sent);
        Assert.Equal(RelayFrame.Register(_room), RelayFrameCodec.Decode(frame));
        Assert.Equal("{\"type\":\"register\",\"room\":\"room-1\"}", System.Text.Encoding.UTF8.GetString(frame));

        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Socket_drop_degrades_and_withdraws_the_offer()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);
        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);
        var socket = factory.Latest();

        socket.Drop();

        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");
        Assert.Equal("relay-disconnected", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.True(socket.Disposed);

        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconnects_after_two_seconds_on_the_fake_clock()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);
        await module.StartAsync(Contexts.Create(time, Settings()), CancellationToken.None);
        factory.Latest().Drop();
        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");

        time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Single(factory.Created);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await RelayTestSupport.Eventually(() => factory.Created.Count == 2, "second socket");
        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Running, "running again");
        Assert.Single(module.Status.Offers);
        Assert.Single(factory.Latest().Sent);

        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Failed_reconnects_back_off_two_four_eight_seconds()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);
        await module.StartAsync(Contexts.Create(time, Settings()), CancellationToken.None);
        factory.FailNextConnects(2);
        factory.Latest().Drop();
        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");

        time.Advance(TimeSpan.FromSeconds(2));
        await RelayTestSupport.Eventually(() => factory.Created.Count == 2, "first retry");
        time.Advance(TimeSpan.FromSeconds(3.9));
        Assert.Equal(2, factory.Created.Count);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await RelayTestSupport.Eventually(() => factory.Created.Count == 3, "second retry");
        time.Advance(TimeSpan.FromSeconds(7.9));
        Assert.Equal(3, factory.Created.Count);

        time.Advance(TimeSpan.FromSeconds(0.1));
        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Running, "running after backoff");
        Assert.Equal(4, factory.Created.Count);

        await module.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Connect_error_fails_with_the_exception_type_name_and_does_not_throw()
    {
        var factory = new FakeRelaySocketFactory();
        factory.FailNextConnects(1);
        var module = new RelayModule(factory.Create);

        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("InvalidOperationException", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.True(factory.Latest().Disposed);
    }

    [Fact]
    public async Task Stop_closes_the_socket_and_withdraws_the_offer()
    {
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);
        await module.StartAsync(Contexts.Create(settings: Settings()), CancellationToken.None);
        var socket = factory.Latest();

        await module.StopAsync(CancellationToken.None);

        Assert.True(socket.Closed);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);
    }

    [Fact]
    public async Task Stop_before_reconnect_prevents_a_new_socket()
    {
        var time = new FakeTimeProvider();
        var factory = new FakeRelaySocketFactory();
        var module = new RelayModule(factory.Create);
        await module.StartAsync(Contexts.Create(time, Settings()), CancellationToken.None);
        factory.Latest().Drop();
        await RelayTestSupport.Eventually(() => module.Status.State == ConduitModuleState.Degraded, "degraded state");

        await module.StopAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(factory.Created);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }
}
