using System.Net;
using Bazaar.Conduit;
using Bazaar.Conduit.LanMdns;
using Bazaar.Conduit.Tests.Support;

namespace Bazaar.Conduit.Tests.LanMdns;

public sealed class LanMdnsModuleTests
{
    private const int _port = 7443;

    [Fact]
    public void Identity_matches_the_catalogue_row()
    {
        var module = new LanMdnsModule(new FakeLanAddressSource(), _ => new FakeMulticastResponder());

        Assert.Equal("lan", module.Id);
        Assert.Equal("LAN discovery (mDNS)", module.DisplayName);
        Assert.Equal(ConduitScope.Lan, module.Scope);
        Assert.Equal(10, module.Priority);
        Assert.True(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task Two_addresses_publish_two_wss_offers_on_the_server_port()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20", "10.0.0.7"), _ => socket);

        await module.StartAsync(Contexts.Create(port: _port), CancellationToken.None);

        var status = module.Status;
        Assert.Equal(ConduitModuleState.Running, status.State);
        Assert.Null(status.Reason);
        Assert.Equal(2, status.Offers.Count);
        Assert.Equal(["wss://192.168.1.20:7443/ws", "wss://10.0.0.7:7443/ws"], status.Offers.Select(o => o.Uri.ToString()));
        foreach (var offer in status.Offers)
        {
            Assert.Equal("lan", offer.ModuleId);
            Assert.Equal(ConduitScope.Lan, offer.Scope);
            Assert.Equal(Contexts.KeyHash, offer.ServerKeyHash);
            Assert.False(offer.Verified);
            Assert.False(offer.RequiresRelay);
            Assert.Null(offer.ExpiresAt);
            Assert.False(offer.TerminatesTls);
        }

        Assert.True(socket.Started);
        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task The_module_never_publishes_a_plain_ws_uri()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20", "172.16.4.9"), _ => socket);

        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.All(module.Status.Offers, offer =>
        {
            Assert.Equal("wss", offer.Uri.Scheme);
            Assert.Null(ConduitOfferRules.Validate(offer));
        });
    }

    [Fact]
    public async Task No_lan_address_fails_with_no_lan_address_and_does_not_open_a_socket()
    {
        var opened = 0;
        var module = new LanMdnsModule(new FakeLanAddressSource(), _ =>
        {
            opened++;
            return new FakeMulticastResponder();
        });

        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("no-lan-address", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task A_socket_that_cannot_bind_fails_with_bind_failed_and_never_throws()
    {
        var socket = new FakeMulticastResponder(failStart: true);
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20"), _ => socket);

        var exception = await Record.ExceptionAsync(() => module.StartAsync(Contexts.Create(), CancellationToken.None).AsTask());

        Assert.Null(exception);
        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("bind-failed", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task Stop_sends_a_goodbye_releases_the_socket_and_clears_offers()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20", "10.0.0.7"), _ => socket);
        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        await module.StopAsync(CancellationToken.None);

        Assert.True(socket.Disposed);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Empty(module.Status.Offers);

        var goodbye = MdnsCodec.Decode(socket.Sent.Last());
        Assert.True(goodbye.IsResponse);
        Assert.Equal(5, goodbye.Answers.Count);
        Assert.All(goodbye.Answers, record => Assert.Equal(0u, record.Ttl));
        Assert.Contains(goodbye.Answers, record => record.Data is PtrRecordData);
        Assert.Equal(2, goodbye.Answers.Count(record => record.Data is ARecordData));
    }

    [Fact]
    public async Task Stop_is_idempotent_and_safe_before_start()
    {
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20"), _ => new FakeMulticastResponder());

        await module.StopAsync(CancellationToken.None);
        await module.StartAsync(Contexts.Create(), CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);

        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task A_ptr_query_for_the_service_gets_the_service_records()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20", "10.0.0.7"), _ => socket);
        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        await socket.DeliverAsync(Query("_bazaar._tcp.local", MdnsTypes.PtrRecordType));

        Assert.Equal(2, socket.Sent.Count);
        var response = MdnsCodec.Decode(socket.Sent[1]);
        Assert.True(response.IsResponse);
        Assert.Equal(0, response.Id);

        var ptr = Assert.Single(response.Answers);
        Assert.Equal("_bazaar._tcp.local", ptr.Name);
        Assert.Equal("bazaar._bazaar._tcp.local", Assert.IsType<PtrRecordData>(ptr.Data).Target);
        Assert.Equal(4500u, ptr.Ttl);

        var srv = Assert.IsType<SrvRecordData>(Assert.Single(response.Additionals, r => r.Data is SrvRecordData).Data);
        Assert.Equal((ushort)_port, srv.Port);
        Assert.Equal("bazaar.local", srv.Target);

        var txt = Assert.IsType<TxtRecordData>(Assert.Single(response.Additionals, r => r.Data is TxtRecordData).Data);
        Assert.Equal(["keyhash=0000000000000000", "ver=0.0.0"], txt.Strings);

        var addresses = response.Additionals
            .Where(r => r.Data is ARecordData)
            .Select(r => ((ARecordData)r.Data).Address.ToString())
            .ToList();
        Assert.Equal(["192.168.1.20", "10.0.0.7"], addresses);
    }

    [Fact]
    public async Task Settings_choose_the_instance_name_and_the_version_in_txt()
    {
        var socket = new FakeMulticastResponder();
        var settings = new Dictionary<string, object>
        {
            ["conduit.lan.instance"] = "office.hub",
            ["server.version"] = "1.2.3",
        };
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20"), _ => socket);
        await module.StartAsync(Contexts.Create(settings: settings), CancellationToken.None);

        await socket.DeliverAsync(Query("_bazaar._tcp.local", MdnsTypes.PtrRecordType));

        var response = MdnsCodec.Decode(socket.Sent[1]);
        Assert.Equal("office-hub._bazaar._tcp.local", Assert.IsType<PtrRecordData>(response.Answers[0].Data).Target);
        var txt = Assert.IsType<TxtRecordData>(Assert.Single(response.Additionals, r => r.Data is TxtRecordData).Data);
        Assert.Contains("ver=1.2.3", txt.Strings);
    }

    [Fact]
    public async Task Queries_for_other_names_and_malformed_packets_get_no_reply()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20"), _ => socket);
        await module.StartAsync(Contexts.Create(), CancellationToken.None);

        await socket.DeliverAsync(Query("_printer._tcp.local", MdnsTypes.PtrRecordType));
        await socket.DeliverAsync([0xFF, 0x00, 0x12]);

        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task A_registry_publishes_the_module_offers_in_order()
    {
        var socket = new FakeMulticastResponder();
        var module = new LanMdnsModule(new FakeLanAddressSource("192.168.1.20"), _ => socket);
        await using var registry = new ConduitRegistry([module], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await registry.StartAsync(Contexts.Create(), CancellationToken.None);

        var offer = Assert.Single(registry.CurrentOffers());
        Assert.Equal("wss://192.168.1.20:7443/ws", offer.Uri.ToString());
    }

    private static byte[] Query(string name, ushort type) =>
        MdnsCodec.Encode(new MdnsMessage(0, false, false, [new MdnsQuestion(name, type, MdnsTypes.ClassIn)], [], [], []));
}
