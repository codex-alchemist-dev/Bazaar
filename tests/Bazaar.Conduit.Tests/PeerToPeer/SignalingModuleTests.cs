using System.Net;
using Bazaar.Conduit.PeerToPeer;
using Bazaar.Conduit.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.PeerToPeer;

public sealed class SignalingModuleTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPEndPoint _mapped = new(IPAddress.Parse("203.0.113.5"), 50000);

    [Fact]
    public async Task StartAsync_WithChannel_ReportsSignalingReady()
    {
        var (channel, _) = InMemoryRendezvousChannel.CreatePair();
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Answering(_mapped)), channel);
        var settings = new Dictionary<string, object> { [P2pIceModule.StunServersKey] = new[] { "192.0.2.10:3478" } };

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(_now), settings), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Starting, module.Status.State);
        Assert.Equal("signaling-ready", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Equal(_mapped, module.LastReflexive!.Endpoint);
    }

    [Fact]
    public async Task StartAsync_WithChannelButNoStunServers_StillFailsOnStun()
    {
        var (channel, _) = InMemoryRendezvousChannel.CreatePair();
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Answering(_mapped)), channel);

        await module.StartAsync(Contexts.Create(new FakeTimeProvider(_now)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("no-stun-servers", module.Status.Reason);
    }
}
