using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bazaar.Conduit.PeerToPeer;

namespace Bazaar.Conduit.Tests.PeerToPeer;

/// <summary>Real-socket tests, all on 127.0.0.1. No test touches the internet.</summary>
public sealed class UdpStunTransportTests
{
    [Fact]
    public async Task ExchangeAsync_LoopbackServerReplies_ReturnsReplyThatDecodes()
    {
        var mapped = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);
        using var server = new StunFixtures.LoopbackStunServer(request =>
            StunFixtures.SuccessResponse(request.AsSpan(8, 12), mapped));
        var request = StunMessage.CreateBindingRequest();

        var reply = await new UdpStunTransport().ExchangeAsync(server.Endpoint, request.Packet, TimeSpan.FromSeconds(3), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(mapped, StunMessage.ReadMappedAddress(reply, request.TransactionId));
    }

    [Fact]
    public async Task ExchangeAsync_NoReply_ReturnsNullWithinBudget()
    {
        using var server = new StunFixtures.LoopbackStunServer(_ => null);
        var request = StunMessage.CreateBindingRequest();
        var clock = Stopwatch.StartNew();

        var reply = await new UdpStunTransport().ExchangeAsync(server.Endpoint, request.Packet, TimeSpan.FromMilliseconds(700), CancellationToken.None);

        Assert.Null(reply);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(600), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExchangeAsync_RetransmitsWhileWaiting()
    {
        // The first retransmission goes out at 500 ms, so a 1.2 s budget sends at least two copies.
        using var server = new StunFixtures.LoopbackStunServer(_ => null);
        var request = StunMessage.CreateBindingRequest();

        await new UdpStunTransport().ExchangeAsync(server.Endpoint, request.Packet, TimeSpan.FromMilliseconds(1200), CancellationToken.None);

        Assert.True(server.RequestCount >= 2, $"expected at least 2 requests, saw {server.RequestCount}");
    }

    [Fact]
    public async Task ExchangeAsync_Cancelled_ThrowsOperationCanceled()
    {
        using var server = new StunFixtures.LoopbackStunServer(_ => null);
        var request = StunMessage.CreateBindingRequest();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UdpStunTransport().ExchangeAsync(server.Endpoint, request.Packet, TimeSpan.FromSeconds(10), cts.Token));
    }
}
