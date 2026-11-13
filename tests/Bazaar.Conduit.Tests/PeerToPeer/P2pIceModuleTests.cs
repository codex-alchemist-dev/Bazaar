using System.Net;
using Bazaar.Conduit.PeerToPeer;
using Bazaar.Conduit.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Conduit.Tests.PeerToPeer;

public sealed class P2pIceModuleTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPEndPoint _mapped = new(IPAddress.Parse("203.0.113.5"), 50000);

    private static ConduitContext Context(IDictionary<string, object>? settings = null) =>
        Contexts.Create(new FakeTimeProvider(_now), settings);

    private static readonly string[] _oneServer = ["192.0.2.10:3478"];

    private static Dictionary<string, object> Servers(object list) =>
        new() { [P2pIceModule.StunServersKey] = list };

    [Fact]
    public void Identity_MatchesDocSection2Row()
    {
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Silent()));

        Assert.Equal("p2p", module.Id);
        Assert.Equal("Peer-to-peer (ICE, STUN stage)", module.DisplayName);
        Assert.Equal(ConduitScope.PeerToPeer, module.Scope);
        Assert.Equal(55, module.Priority);
        Assert.False(module.DefaultEnabled);
        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
    }

    [Fact]
    public async Task StartAsync_NoStunServers_FailsWithNoStunServers()
    {
        var transport = FakeStunTransport.Answering(_mapped);
        var module = new P2pIceModule(new StunGatherer(transport));

        await module.StartAsync(Context(), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("no-stun-servers", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Empty(transport.Asked);
    }

    [Fact]
    public async Task StartAsync_StunAnswers_ReportsStartingWithSignalingNotConfiguredAndNoOffers()
    {
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Answering(_mapped)));

        await module.StartAsync(Context(Servers(_oneServer)), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Starting, module.Status.State);
        Assert.Equal("signaling-not-configured", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.NotNull(module.LastReflexive);
        Assert.Equal(_mapped, module.LastReflexive.Endpoint);
        Assert.Equal(_now, module.LastReflexive.GatheredAt);
    }

    [Fact]
    public async Task StartAsync_StunSilent_FailsWithNoStunResponse()
    {
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Silent()));

        await module.StartAsync(Context(Servers(new List<string> { "192.0.2.10:3478" })), CancellationToken.None);

        Assert.Equal(ConduitModuleState.Failed, module.Status.State);
        Assert.Equal("no-stun-response", module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Null(module.LastReflexive);
    }

    [Fact]
    public async Task StartAsync_PublishesStatusChanges()
    {
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Answering(_mapped)));
        var states = new List<ConduitModuleState>();
        module.StatusChanged += (_, status) => states.Add(status.State);

        await module.StartAsync(Context(Servers(_oneServer)), CancellationToken.None);

        Assert.Equal(new[] { ConduitModuleState.Starting, ConduitModuleState.Starting }, states);
    }

    [Fact]
    public async Task StopAsync_SetsDisabledAndClearsState()
    {
        var module = new P2pIceModule(new StunGatherer(FakeStunTransport.Answering(_mapped)));
        await module.StartAsync(Context(Servers(_oneServer)), CancellationToken.None);

        await module.StopAsync(CancellationToken.None);

        Assert.Equal(ConduitModuleState.Disabled, module.Status.State);
        Assert.Null(module.Status.Reason);
        Assert.Empty(module.Status.Offers);
        Assert.Null(module.LastReflexive);
    }
}
