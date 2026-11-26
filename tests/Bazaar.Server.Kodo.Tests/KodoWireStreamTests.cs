using Bazaar.Client.Kodo;
using Bazaar.Client.Kodo.Live;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Kodo;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

/// <summary>The <c>kodo</c> stream on the existing wire subscription engine (doc 46 section 15.2, PROPOSED).</summary>
public class KodoWireStreamTests
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly JsonWireCodec _codec = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoGcSource : IGcStreamSource, IGcAccessPolicy
    {
        public ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct) => ValueTask.FromResult<GcHead?>(null);

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>([]);

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>([]);

        public ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask<bool> CanReadAsync(Guid principalId, PrincipalKind kind, Guid gcId, CancellationToken ct) => ValueTask.FromResult(false);
    }

    /// <summary>One socket: the engine's session writes into its outbound, the publisher into <see cref="TryEnqueueEvent"/>.</summary>
    private sealed class Socket : IWireConnection, IWireConnectionDirectory, IDisposable
    {
        private readonly BoundedOutbound _outbound;
        private int _dropNext;

        public Socket(SubscriptionEngine engine, PrincipalKind kind, int queueMax = 100)
        {
            _outbound = new BoundedOutbound(queueMax);
            Session = engine.CreateSession(_owner, kind, _codec, _outbound);
        }

        public SubscriptionSession Session { get; }

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal => new(_owner, "owner");

        public string DeviceId => "d";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public ISubscriptionSession? Subscriptions => Session;

        public CancellationToken Closed => CancellationToken.None;

        public IReadOnlyCollection<IWireConnection> Connections => [this];

        /// <summary>Loses the next <paramref name="count"/> live events, as a full queue would.</summary>
        public void DropNext(int count = 1) => _dropNext = count;

        public bool TryEnqueueEvent(Envelope envelope)
        {
            if (_dropNext > 0)
            {
                _dropNext--;
                return false;
            }

            return _outbound.TryEnqueue(envelope);
        }

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;

        public async Task<string> SubscribeAsync()
        {
            var outcome = await Session.SubscribeAsync(new Subscribe([new StreamCursor(WireStreams.Kodo, 0, 0)]), Ct);
            await outcome.StartAsync();
            return Assert.Single(outcome.Result.Results).Status;
        }

        /// <summary>Takes every frame queued so far.</summary>
        public List<Envelope> Drain()
        {
            var frames = new List<Envelope>();
            while (_outbound.Depth > 0)
            {
                var read = _outbound.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
                Assert.True(read.MoveNextAsync().AsTask().Result);
                frames.Add(read.Current);
            }

            return frames;
        }

        public void Dispose()
        {
            Session.Dispose();
            _outbound.Dispose();
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly NoGcSource _gc = new();
        private readonly FakeReadings _readings = new();

        public Rig(IKodoStreamAccess? access = null, bool enabled = true)
        {
            Time = Make.Clock();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<Bazaar.Kodo.Probes.Host.IHostReadings>(_readings);
            services.AddSingleton<IKodoStreamPublisher>(Publisher);
            if (access is not null)
            {
                services.AddSingleton(access);
            }

            services.AddBazaarKodo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Kodo:Enabled"] = enabled ? "true" : "false" }).Build());
            Provider = services.BuildServiceProvider();
            Gates = [.. Provider.GetServices<IStreamGate>()];
            Engine = new SubscriptionEngine(_gc, _gc, new GcDispatcher(_gc, new SyncOptions()), new SyncOptions(), Time, null, Gates);
        }

        public FakeTimeProvider Time { get; }

        public ServiceProvider Provider { get; }

        public IStreamGate[] Gates { get; }

        public SubscriptionEngine Engine { get; }

        public SwitchPublisher Publisher { get; } = new();

        public KodoMonitor Monitor => (KodoMonitor)Provider.GetRequiredService<IKodoStatusSource>();

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>Forwards to the wire publisher once a socket is attached, so a test can build the monitor first.</summary>
    private sealed class SwitchPublisher : IKodoStreamPublisher
    {
        public KodoWireStreamPublisher? Target { get; set; }

        public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken) =>
            Target?.PublishAsync(streamEvent, cancellationToken) ?? ValueTask.CompletedTask;
    }

    /// <summary>Reads a frame with the server codec and maps it to the shape the client tracker consumes.</summary>
    private static KodoFrameWire Read(Envelope frame)
    {
        Assert.Equal(WireTypes.KodoEvent, frame.T);
        Assert.Equal(WireStreams.Kodo, frame.S);
        Assert.Equal(EnvelopeKind.Event, frame.K);
        Assert.True(KodoStreamCodec.TryReadFrame(frame.P.Span, out KodoFrameWire? read, out string? error), error);
        return read!;
    }

    [Fact]
    public async Task Subscribe_sends_the_snapshot_first_at_the_current_revision()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);

        Assert.Equal(SubscribeStatuses.Ok, await socket.SubscribeAsync());

        var frame = Read(Assert.Single(socket.Drain()));
        Assert.Equal("snapshot", frame.Type);
        Assert.Equal(rig.Monitor.Latest!.Revision, frame.Revision);
        Assert.Equal(frame.Revision, frame.Snapshot!.Revision);
        Assert.Equal([WireStreams.Kodo], socket.Session.Streams);
    }

    [Fact]
    public async Task Deltas_follow_the_snapshot_in_gap_free_order_and_the_client_applies_them()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        rig.Publisher.Target = new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time);
        await socket.SubscribeAsync();

        for (var i = 0; i < 3; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(11));
            await rig.Monitor.RunCycleAsync(Ct);
        }

        var session = new KodoStreamSession(rig.Time);
        var frames = socket.Drain().Select(Read).ToList();
        Assert.Equal(["snapshot", "heartbeat", "heartbeat", "heartbeat"], frames.Select(f => f.Type));
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.Equal(frames[i - 1].Revision + 1, frames[i].Revision);
        }

        foreach (var frame in frames)
        {
            Assert.Equal(KodoApplyOutcome.Applied, session.Apply(frame).Outcome);
        }

        Assert.False(session.NeedsSnapshot);
        Assert.Equal(frames[^1].Revision, session.Revision);
    }

    [Fact]
    public async Task A_state_change_is_pushed_with_its_previous_and_current_state()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        rig.Publisher.Target = new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time);
        await socket.SubscribeAsync();

        // The directory publisher is exercised directly with a state change, as the monitor numbers it.
        var change = new KodoStreamEventDto(5, KodoStreamEventDto.StateChangedType, rig.Time.GetUtcNow(), new KodoStateChangeDto("healthy", "degraded", null), null);
        await rig.Publisher.PublishAsync(change, Ct);

        var frames = socket.Drain().Select(Read).ToList();
        var delta = Assert.Single(frames, f => f.Type == "state_changed");
        Assert.Equal(5, delta.Revision);
        Assert.Equal("healthy", delta.StateChange!.Previous);
        Assert.Equal("degraded", delta.StateChange.State);
    }

    [Fact]
    public async Task A_lost_delta_shows_as_a_gap_and_subscribing_again_sends_a_fresh_snapshot()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        rig.Publisher.Target = new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time);
        await socket.SubscribeAsync();
        var session = new KodoStreamSession(rig.Time);
        foreach (var frame in socket.Drain().Select(Read))
        {
            session.Apply(frame);
        }

        socket.DropNext();
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Monitor.RunCycleAsync(Ct);
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Monitor.RunCycleAsync(Ct);

        var after = Assert.Single(socket.Drain().Select(Read));
        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, session.Apply(after).Outcome);
        Assert.True(session.NeedsSnapshot);

        Assert.Equal(SubscribeStatuses.Ok, await socket.SubscribeAsync());
        var fresh = Assert.Single(socket.Drain().Select(Read));
        Assert.Equal("snapshot", fresh.Type);
        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(fresh).Outcome);
        Assert.False(session.NeedsSnapshot);
        Assert.Equal(after.Revision, session.Revision);
    }

    [Fact]
    public async Task Connections_that_did_not_subscribe_get_nothing()
    {
        using var rig = new Rig();
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        rig.Publisher.Target = new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time);

        await rig.Monitor.RunCycleAsync(Ct);

        Assert.Empty(socket.Drain());
    }

    [Theory]
    [InlineData(PrincipalKind.Human)]
    [InlineData(PrincipalKind.Agent)]
    public async Task Only_the_owner_may_subscribe_by_default(PrincipalKind kind)
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, kind);

        Assert.Equal(SubscribeStatuses.Denied, await socket.SubscribeAsync());
        Assert.Empty(socket.Session.Streams);
        Assert.Empty(socket.Drain());
    }

    [Fact]
    public async Task A_host_access_check_replaces_the_default()
    {
        using var rig = new Rig(new AllowHumans());
        await rig.Monitor.RunCycleAsync(Ct);
        using var human = new Socket(rig.Engine, PrincipalKind.Human);
        using var agent = new Socket(rig.Engine, PrincipalKind.Agent);

        Assert.Equal(SubscribeStatuses.Ok, await human.SubscribeAsync());
        Assert.Equal(SubscribeStatuses.Denied, await agent.SubscribeAsync());
    }

    private sealed class AllowHumans : IKodoStreamAccess
    {
        public ValueTask<bool> CanViewAsync(Guid principalId, PrincipalKind principalKind, CancellationToken cancellationToken) =>
            ValueTask.FromResult(principalKind != PrincipalKind.Agent);
    }

    [Fact]
    public async Task Before_the_first_evaluation_the_subscribe_is_ok_but_no_snapshot_is_sent()
    {
        using var rig = new Rig();
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);

        Assert.Equal(SubscribeStatuses.Ok, await socket.SubscribeAsync());
        Assert.Empty(socket.Drain());
    }

    [Fact]
    public async Task Unsubscribing_stops_the_deltas()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        rig.Publisher.Target = new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time);
        await socket.SubscribeAsync();
        socket.Drain();

        socket.Session.Unsubscribe([WireStreams.Kodo]);
        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Monitor.RunCycleAsync(Ct);

        Assert.Empty(socket.Drain());
    }

    [Fact]
    public async Task A_snapshot_event_is_never_broadcast()
    {
        using var rig = new Rig();
        await rig.Monitor.RunCycleAsync(Ct);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);
        await socket.SubscribeAsync();
        socket.Drain();

        await new KodoWireStreamPublisher(socket, new SyncOptions(), rig.Time)
            .PublishAsync(new KodoStreamEventDto(1, KodoStreamEventDto.SnapshotType, rig.Time.GetUtcNow(), null, rig.Monitor.Latest), Ct);

        Assert.Empty(socket.Drain());
    }

    [Fact]
    public async Task A_publisher_with_no_wire_directory_drops_events_without_failing()
    {
        var publisher = new KodoWireStreamPublisher(null, new SyncOptions(), TimeProvider.System);

        await publisher.PublishAsync(new KodoStreamEventDto(1, KodoStreamEventDto.HeartbeatType, DateTimeOffset.UnixEpoch, null, null), Ct);
    }

    [Fact]
    public void Registration_exposes_the_gate_and_the_wire_publisher()
    {
        using var rig = new Rig();

        Assert.Contains(rig.Gates, g => g is KodoStreamGate { Stream: WireStreams.Kodo });
        Assert.IsType<OwnerOnlyKodoStreamAccess>(rig.Provider.GetRequiredService<IKodoStreamAccess>());
    }

    [Fact]
    public void The_default_publisher_is_the_wire_publisher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Make.Clock());
        services.AddBazaarKodo();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<KodoWireStreamPublisher>(provider.GetRequiredService<IKodoStreamPublisher>());
    }

    [Fact]
    public async Task A_switched_off_module_registers_no_gate_so_the_stream_is_denied()
    {
        using var rig = new Rig(enabled: false);
        using var socket = new Socket(rig.Engine, PrincipalKind.Owner);

        Assert.Empty(rig.Gates);
        Assert.Equal(SubscribeStatuses.Denied, await socket.SubscribeAsync());
    }

    [Fact]
    public async Task A_gate_over_a_disabled_source_refuses_everyone()
    {
        var gate = new KodoStreamGate(new DisabledKodoStatusSource(), new AllowHumans(), TimeProvider.System);

        Assert.False(await gate.CanSubscribeAsync(_owner, PrincipalKind.Owner, Ct));
        Assert.Null(await gate.OpenAsync(_owner, PrincipalKind.Owner, Ct));
    }
}
