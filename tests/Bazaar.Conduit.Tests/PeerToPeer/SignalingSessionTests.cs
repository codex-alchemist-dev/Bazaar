using System.Net;
using Bazaar.Conduit.PeerToPeer;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.PeerToPeer;

public sealed class SignalingSessionTests
{
    private static readonly string _sessionId = "rendezvous-1";
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private static readonly IPEndPoint _alice = new(IPAddress.Parse("203.0.113.5"), 50000);
    private static readonly IPEndPoint _bob = new(IPAddress.Parse("198.51.100.9"), 61000);

    [Fact]
    public async Task TwoSessions_ExchangeCandidates_OverInMemoryPair()
    {
        var (aliceChannel, bobChannel) = InMemoryRendezvousChannel.CreatePair();
        var alice = new SignalingSession(aliceChannel, _sessionId, TimeProvider.System, _timeout);
        var bob = new SignalingSession(bobChannel, _sessionId, TimeProvider.System, _timeout);

        var aliceTask = alice.ExchangeCandidatesAsync(_alice, CancellationToken.None);
        var bobTask = bob.ExchangeCandidatesAsync(_bob, CancellationToken.None);
        var results = await Task.WhenAll(aliceTask, bobTask);

        Assert.Null(results[0].Reason);
        Assert.Equal(_alice, results[0].Pair!.Local);
        Assert.Equal(_bob, results[0].Pair!.Remote);

        Assert.Null(results[1].Reason);
        Assert.Equal(_bob, results[1].Pair!.Local);
        Assert.Equal(_alice, results[1].Pair!.Remote);
    }

    [Fact]
    public async Task ExchangeCandidates_SendsOurEndpointAsCandidateMessage()
    {
        var (channel, peer) = InMemoryRendezvousChannel.CreatePair();
        var session = new SignalingSession(channel, _sessionId, new FakeTimeProvider(_now), _timeout);

        var pending = session.ExchangeCandidatesAsync(_alice, CancellationToken.None);
        peer.Close();
        await pending;

        var sent = Assert.Single(channel.Sent);
        Assert.Equal(SignalKinds.Candidate, sent.Kind);
        Assert.Equal(_sessionId, sent.SessionId);
        Assert.Equal("203.0.113.5:50000", sent.Payload);
    }

    [Fact]
    public async Task MissingPeer_TimesOutOnFakeClock()
    {
        var time = new FakeTimeProvider(_now);
        var (channel, _) = InMemoryRendezvousChannel.CreatePair();
        var session = new SignalingSession(channel, _sessionId, time, _timeout);

        var pending = session.ExchangeCandidatesAsync(_alice, CancellationToken.None);
        time.Advance(_timeout);
        var result = await pending;

        Assert.Null(result.Pair);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task PeerClosesChannel_ReportsNoPeerCandidate()
    {
        var time = new FakeTimeProvider(_now);
        var (channel, peer) = InMemoryRendezvousChannel.CreatePair();
        var session = new SignalingSession(channel, _sessionId, time, _timeout);
        peer.Close();

        var result = await session.ExchangeCandidatesAsync(_alice, CancellationToken.None);

        Assert.Null(result.Pair);
        Assert.Equal("no-peer-candidate", result.Reason);
    }

    [Fact]
    public async Task PeerSendsEnd_ReportsNoPeerCandidate()
    {
        var time = new FakeTimeProvider(_now);
        var (channel, peer) = InMemoryRendezvousChannel.CreatePair();
        var session = new SignalingSession(channel, _sessionId, time, _timeout);
        await peer.SendAsync(new SignalMessage(SignalKinds.End, _sessionId, "bye"), CancellationToken.None);

        var result = await session.ExchangeCandidatesAsync(_alice, CancellationToken.None);

        Assert.Equal("no-peer-candidate", result.Reason);
    }

    [Fact]
    public async Task MessagesForOtherSessions_AreIgnored()
    {
        var (aliceChannel, bobChannel) = InMemoryRendezvousChannel.CreatePair();
        var alice = new SignalingSession(aliceChannel, _sessionId, TimeProvider.System, _timeout);

        await bobChannel.SendAsync(new SignalMessage(SignalKinds.Candidate, "someone-else", _bob.ToString()), CancellationToken.None);
        await bobChannel.SendAsync(new SignalMessage(SignalKinds.Candidate, _sessionId, _bob.ToString()), CancellationToken.None);

        var result = await alice.ExchangeCandidatesAsync(_alice, CancellationToken.None);

        Assert.Null(result.Reason);
        Assert.Equal(_bob, result.Pair!.Remote);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveTimeout()
    {
        var (channel, _) = InMemoryRendezvousChannel.CreatePair();

        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalingSession(channel, _sessionId, TimeProvider.System, TimeSpan.Zero));
    }
}
