using System.Net;
using System.Net.Sockets;
using Bazaar.Server.Transports.PeerToPeer;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Transports.Tests.PeerToPeer;

public sealed class StunGathererTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPEndPoint _mapped = new(IPAddress.Parse("203.0.113.5"), 50000);
    private static readonly string[] _silentThenWorking = ["192.0.2.10:3478", "192.0.2.11:3479"];
    private static readonly string[] _twoSilent = ["192.0.2.10:3478", "192.0.2.11:3478"];
    private static readonly string[] _oneSilent = ["192.0.2.10:3478"];
    private static readonly string[] _brokenThenGood = ["broken.example.test:3478", "stun.example.test:3478"];
    private static readonly string[] _bracketed = ["[2001:db8::1]:3478"];

    private static Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken) => host switch
    {
        "stun.example.test" => Task.FromResult(new[] { IPAddress.Parse("198.51.100.20") }),
        _ => throw new SocketException((int)SocketError.HostNotFound),
    };

    [Fact]
    public async Task GatherAsync_FirstServerSilent_TriesNextServerAndReturnsCandidate()
    {
        var transport = new FakeStunTransport((server, request) =>
            server.Port == 3479 ? StunFixtures.SuccessResponse(request.AsSpan(8, 12), _mapped) : null);
        var gatherer = new StunGatherer(transport);
        var time = new FakeTimeProvider(_now);

        var result = await gatherer.GatherAsync(_silentThenWorking, time, CancellationToken.None);

        Assert.NotNull(result.Candidate);
        Assert.Null(result.Reason);
        Assert.Equal(_mapped, result.Candidate.Endpoint);
        Assert.Equal("192.0.2.11:3479", result.Candidate.Server);
        Assert.Equal(_now, result.Candidate.GatheredAt);
        Assert.Equal(_silentThenWorking, transport.Asked.Select(e => e.ToString()).ToArray());
    }

    [Fact]
    public async Task GatherAsync_AllServersFail_ReturnsNoStunResponse()
    {
        var transport = FakeStunTransport.Silent();
        var gatherer = new StunGatherer(transport);

        var result = await gatherer.GatherAsync(_twoSilent, new FakeTimeProvider(_now), CancellationToken.None);

        Assert.Null(result.Candidate);
        Assert.Equal("no-stun-response", result.Reason);
        Assert.Equal(2, transport.Asked.Count);
    }

    [Fact]
    public async Task GatherAsync_MismatchedTransactionId_CountsAsFailure()
    {
        var transport = new FakeStunTransport((_, _) =>
            StunFixtures.SuccessResponse(new byte[12], _mapped));
        var gatherer = new StunGatherer(transport);

        var result = await gatherer.GatherAsync(_oneSilent, new FakeTimeProvider(_now), CancellationToken.None);

        Assert.Null(result.Candidate);
        Assert.Equal("no-stun-response", result.Reason);
    }

    [Fact]
    public async Task GatherAsync_ResolutionFailureAffectsOnlyThatServer()
    {
        var transport = FakeStunTransport.Answering(_mapped);
        var gatherer = new StunGatherer(transport, Resolve);

        var result = await gatherer.GatherAsync(_brokenThenGood, new FakeTimeProvider(_now), CancellationToken.None);

        Assert.NotNull(result.Candidate);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 3478), transport.Asked.Single());
    }

    [Fact]
    public async Task GatherAsync_TransportThrows_CountsAsFailureAndContinues()
    {
        var calls = 0;
        var transport = new FakeStunTransport((server, request) =>
        {
            calls++;
            if (server.Port == 3478)
            {
                throw new SocketException();
            }

            return StunFixtures.SuccessResponse(request.AsSpan(8, 12), _mapped);
        });
        var gatherer = new StunGatherer(transport);

        var result = await gatherer.GatherAsync(_silentThenWorking, new FakeTimeProvider(_now), CancellationToken.None);

        Assert.NotNull(result.Candidate);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stun.example.test")]
    [InlineData("stun.example.test:0")]
    [InlineData("stun.example.test:70000")]
    [InlineData("stun.example.test:http")]
    [InlineData("[2001:db8::1]")]
    [InlineData("2001:db8::1:3478")]
    [InlineData(":3478")]
    public async Task GatherAsync_InvalidServerEntries_AreSkipped(string entry)
    {
        var transport = FakeStunTransport.Answering(_mapped);
        var gatherer = new StunGatherer(transport, Resolve);

        var result = await gatherer.GatherAsync([entry], new FakeTimeProvider(_now), CancellationToken.None);

        Assert.Null(result.Candidate);
        Assert.Equal("no-stun-response", result.Reason);
        Assert.Empty(transport.Asked);
    }

    [Fact]
    public async Task GatherAsync_BracketedIpv6Server_IsParsedAndAsked()
    {
        var transport = FakeStunTransport.Answering(_mapped);
        var gatherer = new StunGatherer(transport);

        var result = await gatherer.GatherAsync(_bracketed, new FakeTimeProvider(_now), CancellationToken.None);

        Assert.NotNull(result.Candidate);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), transport.Asked.Single().Address);
        Assert.Equal(3478, transport.Asked.Single().Port);
    }
}
