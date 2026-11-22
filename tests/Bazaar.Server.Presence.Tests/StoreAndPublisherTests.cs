using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Fakes;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Tests;

/// <summary>The stores, the publisher and the snapshot handler (docs/design/23).</summary>
public class StoreAndPublisherTests
{
    private static readonly JsonWireCodec _codec = new();
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static PresenceRow Row(Guid agent, long revision, string presence = "working") =>
        new(agent, revision, PresenceAvailability.OnShift, PresenceOverlay.Throttled | PresenceOverlay.Idle, presence, PresenceActivityKind.Idle, null, null, null, _t, _t);

    /// <summary>A cache that is down: every call throws.</summary>
    private sealed class DownCache : ICacheProvider
    {
        public string Kind => "down";

        public ProviderStatus Probe() => new("down", false, [], "down");

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct) => throw new IOException("cache down");

        public Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct) => throw new IOException("cache down");

        public Task DeleteAsync(string key, CancellationToken ct) => throw new IOException("cache down");
    }

    private sealed class Subscriptions(params string[] streams) : ISubscriptionSession
    {
        public IReadOnlyCollection<string> Streams => streams;

        public Task<SubscribeOutcome> SubscribeAsync(Subscribe request, CancellationToken ct) => throw new NotSupportedException();

        public void Unsubscribe(IEnumerable<string> streams) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class Connection(string kind, params string[] streams) : IWireConnection
    {
        public List<Envelope> Received { get; } = [];

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal { get; } = new(Guid.NewGuid(), kind);

        public string DeviceId => "d";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public ISubscriptionSession? Subscriptions { get; } = new Subscriptions(streams);

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope)
        {
            Received.Add(envelope);
            return true;
        }

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class Directory(params Connection[] all) : IWireConnectionDirectory
    {
        public IReadOnlyCollection<IWireConnection> Connections => all;
    }

    [Fact]
    public async Task In_memory_store_refuses_an_equal_or_lower_revision()
    {
        var store = new InMemoryPresenceStore();
        var ada = PresenceRig.Ada;

        Assert.True(await store.UpsertAsync(Row(ada, 3), TestContext.Current.CancellationToken));
        Assert.False(await store.UpsertAsync(Row(ada, 3, "thinking"), TestContext.Current.CancellationToken));
        Assert.False(await store.UpsertAsync(Row(ada, 2, "thinking"), TestContext.Current.CancellationToken));
        Assert.True(await store.UpsertAsync(Row(ada, 4, "thinking"), TestContext.Current.CancellationToken));

        Assert.Equal("thinking", (await store.GetAsync(ada, TestContext.Current.CancellationToken))!.Presence);
        Assert.Null(await store.GetAsync(PresenceRig.Bob, TestContext.Current.CancellationToken));
        Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_store_writes_the_row_to_the_cache_and_reads_it_back_after_a_restart()
    {
        var cache = new FakeCacheProvider();
        var options = new PresenceOptions();
        var first = new CacheSnapshotPresenceStore(new InMemoryPresenceStore(), cache, options);
        await first.UpsertAsync(Row(PresenceRig.Ada, 7, "thinking"), TestContext.Current.CancellationToken);

        // A restarted process has an empty in-memory store but the same cache.
        var second = new CacheSnapshotPresenceStore(new InMemoryPresenceStore(), cache, options);
        var row = await second.GetAsync(PresenceRig.Ada, TestContext.Current.CancellationToken);

        Assert.Equal(Row(PresenceRig.Ada, 7, "thinking"), row);
        Assert.Empty(await second.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_restarted_projector_continues_revisions_from_the_cache_snapshot()
    {
        var cache = new FakeCacheProvider();
        var before = new PresenceRig(store: new CacheSnapshotPresenceStore(new InMemoryPresenceStore(), cache, new PresenceOptions()));
        await before.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await before.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall));

        var after = new PresenceRig(store: new CacheSnapshotPresenceStore(new InMemoryPresenceStore(), cache, new PresenceOptions()));
        await after.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(3), PresenceActivityKind.Idle));

        Assert.Equal(3, (await after.RowAsync(PresenceRig.Ada)).Revision);
    }

    [Fact]
    public async Task A_cache_that_is_down_degrades_silently_and_presence_keeps_working()
    {
        var store = new CacheSnapshotPresenceStore(new InMemoryPresenceStore(), new DownCache(), new PresenceOptions());
        var rig = new PresenceRig(store: store);

        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall));

        Assert.Equal("thinking", (await rig.RowAsync(PresenceRig.Ada)).Presence);
        Assert.Single(await store.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(PresenceRig.Bob, TestContext.Current.CancellationToken));
        Assert.True(store.CacheFailures >= 3);
    }

    [Fact]
    public async Task Publisher_sends_a_delta_with_agent_id_and_revision_only_to_connections_subscribed_to_presence()
    {
        var subscribed = new Connection("human", WireStreams.Presence);
        var other = new Connection("human", "gc:00000000-0000-7000-8000-0000000000c1");
        var publisher = new PresencePublisher(new InMemoryPresenceStore(), new Directory(subscribed, other), new SyncOptions(), TimeProvider.System);

        await publisher.PublishDeltaAsync(Row(PresenceRig.Ada, 5, "thinking"), TestContext.Current.CancellationToken);

        Assert.Empty(other.Received);
        var frame = Assert.Single(subscribed.Received);
        Assert.Equal(WireTypes.PresenceUpdate, frame.T);
        Assert.Equal(EnvelopeKind.Event, frame.K);
        Assert.Equal(WireStreams.Presence, frame.S);
        var update = _codec.DecodePayload<PresenceUpdate>(frame);
        Assert.Equal((PresenceKinds.Agent, PresenceRig.Ada, 5L), (update.Kind, update.AgentId, update.Revision));
        Assert.Equal("thinking", update.Row.Presence);
        Assert.Equal(["Throttled", "Idle"], update.Row.Overlays);
        Assert.Equal(PresenceKinds.Agent, _codec.DecodePayload<PresenceTyping>(frame).Kind);
    }

    [Fact]
    public async Task Publisher_with_no_wire_directory_does_nothing()
    {
        var publisher = new PresencePublisher(new InMemoryPresenceStore(), null, new SyncOptions(), TimeProvider.System);

        await publisher.PublishDeltaAsync(Row(PresenceRig.Ada, 1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Snapshot_answers_every_row_with_its_revision()
    {
        var store = new InMemoryPresenceStore();
        await store.UpsertAsync(Row(PresenceRig.Ada, 4), TestContext.Current.CancellationToken);
        await store.UpsertAsync(Row(PresenceRig.Bob, 9, "sleeping"), TestContext.Current.CancellationToken);
        var publisher = new PresencePublisher(store, null, new SyncOptions(), TimeProvider.System);
        var handler = new PresenceSnapshotHandler(publisher);
        var human = new Connection("human");

        var reply = await handler.HandleAsync(
            new WireRequestContext(human, new Envelope(1, WireTypes.PresenceSnapshot, EnvelopeKind.Request, "1", null, null, null, null, _codec.EncodePayload(new PresenceSnapshotRequest()))),
            TestContext.Current.CancellationToken);

        Assert.True(reply.IsOk);
        var snapshot = _codec.DecodePayload<PresenceSnapshot>(new Envelope(1, "r", EnvelopeKind.Response, null, null, null, null, null, reply.Payload));
        Assert.Equal([(PresenceRig.Ada, 4L), (PresenceRig.Bob, 9L)], snapshot.Rows.Select(r => (r.AgentId, r.Revision)).Order());
    }

    [Fact]
    public async Task Snapshot_is_refused_for_an_agent_principal()
    {
        var handler = new PresenceSnapshotHandler(new PresencePublisher(new InMemoryPresenceStore(), null, new SyncOptions(), TimeProvider.System));

        var reply = await handler.HandleAsync(
            new WireRequestContext(new Connection("agent"), new Envelope(1, WireTypes.PresenceSnapshot, EnvelopeKind.Request, "1", null, null, null, null, _codec.EncodePayload(new PresenceSnapshotRequest()))),
            TestContext.Current.CancellationToken);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public async Task Projector_to_publisher_end_to_end_reaches_a_subscribed_connection()
    {
        var store = new InMemoryPresenceStore();
        var connection = new Connection("human", WireStreams.Presence);
        var publisher = new PresencePublisher(store, new Directory(connection), new SyncOptions(), TimeProvider.System);
        var projector = new ActivityProjector(store, publisher, TimeProvider.System, new PresenceOptions());

        await projector.OnEventAsync(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, _t, PresenceAvailability.OnShift), TestContext.Current.CancellationToken);
        await projector.OnEventAsync(ActivityEvent.TurnChanged(PresenceRig.Ada, _t.AddSeconds(1), PresenceActivityKind.ModelCall), TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], connection.Received.Select(f => _codec.DecodePayload<PresenceUpdate>(f).Revision));
        Assert.Equal("thinking", _codec.DecodePayload<PresenceUpdate>(connection.Received[^1]).Row.Presence);
    }
}
