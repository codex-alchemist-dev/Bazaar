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

    private sealed class OpeningGate(string stream, bool nothing = false) : IStreamGate
    {
        private int _opened;

        public string Stream => stream;

        public ValueTask<bool> CanSubscribeAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
            ValueTask.FromResult(principalKind != PrincipalKind.Agent);

        public ValueTask<StreamOpening?> OpenAsync(Guid principalId, PrincipalKind principalKind, CancellationToken ct) =>
            ValueTask.FromResult<StreamOpening?>(nothing ? null : new StreamOpening("opening.test", System.Text.Encoding.UTF8.GetBytes($"open-{++_opened}")));
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

    [Fact]
    public async Task A_gate_opening_event_follows_the_subscribe_and_is_sent_again_on_resubscribe()
    {
        using var h = new SyncHarness();
        using var c = new SyncHarness.Connection(EngineWith(h, new OpeningGate("s1")), SyncHarness.Alice, 50, paused: false);

        Assert.Equal(SubscribeStatuses.Ok, await SubscribeAsync(c, "s1"));
        Assert.Equal(SubscribeStatuses.Ok, await SubscribeAsync(c, "s1"));

        var frames = c.Of("opening.test");
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal("s1", f.S));
        Assert.Equal(["open-1", "open-2"], frames.Select(f => System.Text.Encoding.UTF8.GetString(f.P.Span)));
        Assert.Equal(["s1"], c.Session.Streams);
    }

    [Fact]
    public async Task A_gate_with_no_opening_sends_nothing_and_a_denied_gate_never_opens()
    {
        using var h = new SyncHarness();
        using var quiet = new SyncHarness.Connection(EngineWith(h, new OpeningGate("s1", nothing: true)), SyncHarness.Alice, 50, paused: false);
        using var agent = new SyncHarness.Connection(EngineWith(h, new OpeningGate("s1")), SyncHarness.Alice, 50, paused: false, PrincipalKind.Agent);

        Assert.Equal(SubscribeStatuses.Ok, await SubscribeAsync(quiet, "s1"));
        Assert.Equal(SubscribeStatuses.Denied, await SubscribeAsync(agent, "s1"));

        Assert.Empty(quiet.Frames);
        Assert.Empty(agent.Frames);
    }
}
