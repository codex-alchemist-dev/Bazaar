using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;

namespace Bazaar.Server.Tests.Wire.Sync;

/// <summary>Non-GC streams such as <c>presence</c> are open only through a registered <see cref="IStreamGate"/>.</summary>
public class StreamGateSubscriptionTests
{
    private sealed class HumansOnlyGate(string stream) : IStreamGate
    {
        public string Stream => stream;

        public ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
            ValueTask.FromResult(principalKind != PrincipalKind.Agent);
    }

    private static SubscriptionEngine EngineWith(SyncHarness h, params IStreamGate[] gates) =>
        new(h.Log, h.Log, h.Dispatcher, h.Options, null, null, gates);

    private static async Task<string> SubscribeAsync(SyncHarness.Connection c, string stream)
    {
        var outcome = await c.Session.SubscribeAsync(new Subscribe([new StreamCursor(stream, 0, 0)]), CancellationToken.None);
        await outcome.StartAsync();
        await c.SettleAsync();
        return Assert.Single(outcome.Result.Results).Status;
    }

    [Fact]
    public async Task Without_a_gate_the_presence_stream_is_denied()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(h.Engine, SyncHarness.Alice, 50, paused: false);

        Assert.Equal(SubscribeStatuses.Denied, await SubscribeAsync(c, WireStreams.Presence));
        Assert.Empty(c.Session.Streams);
    }

    [Fact]
    public async Task A_human_is_ok_and_the_stream_is_listed()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, new HumansOnlyGate(WireStreams.Presence)), SyncHarness.Alice, 50, paused: false);

        Assert.Equal(SubscribeStatuses.Ok, await SubscribeAsync(c, WireStreams.Presence));
        Assert.Equal([WireStreams.Presence], c.Session.Streams);
    }

    [Fact]
    public async Task An_agent_is_denied_and_nothing_is_recorded()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, new HumansOnlyGate(WireStreams.Presence)), SyncHarness.Alice, 50, paused: false, PrincipalKind.Agent);

        Assert.Equal(SubscribeStatuses.Denied, await SubscribeAsync(c, WireStreams.Presence));
        Assert.Empty(c.Session.Streams);
    }

    [Fact]
    public async Task Unsubscribe_removes_the_gated_stream()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, new HumansOnlyGate(WireStreams.Presence)), SyncHarness.Alice, 50, paused: false);
        await SubscribeAsync(c, WireStreams.Presence);

        c.Session.Unsubscribe([WireStreams.Presence]);

        Assert.Empty(c.Session.Streams);
    }

    [Fact]
    public async Task A_gate_for_another_stream_does_not_open_presence()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, new HumansOnlyGate("other")), SyncHarness.Alice, 50, paused: false);

        Assert.Equal(SubscribeStatuses.Denied, await SubscribeAsync(c, WireStreams.Presence));
    }
}
