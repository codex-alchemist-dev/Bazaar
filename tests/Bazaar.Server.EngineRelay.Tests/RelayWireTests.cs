using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.EngineRelay.Tests;

public sealed class RelayWireTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly IWireCodec _codec = new JsonWireCodec();

    private sealed class Subs(params string[] streams) : ISubscriptionSession
    {
        public IReadOnlyCollection<string> Streams { get; } = streams;

        public Task<SubscribeOutcome> SubscribeAsync(Subscribe request, CancellationToken ct) => throw new NotSupportedException();

        public void Unsubscribe(IEnumerable<string> streams) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class Conn(string device, string kind = "owner", string deviceClass = "windows", bool subscribed = true) : IWireConnection, IDisposable
    {
        private readonly CancellationTokenSource _closed = new();

        public ConcurrentQueue<Envelope> Events { get; } = new();

        public bool QueueFull { get; set; }

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal { get; } = new(Guid.NewGuid(), kind);

        public string DeviceId { get; } = device;

        public string DeviceClass { get; } = deviceClass;

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors { get; } = [];

        public ISubscriptionSession? Subscriptions { get; } = subscribed ? new Subs(EngineRelayWire.Stream) : new Subs();

        public CancellationToken Closed => _closed.Token;

        public void Close() => _closed.Cancel();

        public void Dispose() => _closed.Dispose();

        public bool TryEnqueueEvent(Envelope envelope)
        {
            if (QueueFull)
            {
                return false;
            }

            Events.Enqueue(envelope);
            return true;
        }

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    private sealed class Directory(params Conn[] connections) : IWireConnectionDirectory
    {
        public IReadOnlyCollection<IWireConnection> Connections { get; } = connections;
    }

    private sealed class Grants : IEngineGrantReader
    {
        public ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct) =>
            ValueTask.FromResult(true);
    }

    private sealed class Taint : ISessionTaintReader
    {
        public ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private sealed class Recorder : IEngineGcRecorder
    {
        public Task RecordAsync(EngineToolCallRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Audit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static (EngineRelayBroker Broker, IWireRequestHandler Handler) Compose(params Conn[] connections)
    {
        var services = new ServiceCollection();
        var directory = new Directory(connections);
        services.AddSingleton<IWireConnectionDirectory>(directory);
        services.AddSingleton(new EngineToolDispatcher(new Grants(), new Taint(), new Recorder(), new Audit(), new EngineDispatchOptions { RobloxExecOwnerApproval = false }));
        services.AddSingleton(sp => new EngineRelayBroker(
            sp.GetRequiredService<EngineToolDispatcher>(),
            sp.GetRequiredService<IRelayTargetDirectory>(),
            sp.GetRequiredService<IRelayPublisher>(),
            new EngineRelayOptions { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddBazaarEngineRelay();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<EngineRelayBroker>(), provider.GetServices<IWireRequestHandler>().Single(h => h.Type == EngineRelayWire.RequestType));
    }

    private static EngineRelayRequestEvent DecodeEvent(Envelope envelope) =>
        EngineRelayFrameMapping.ToEvent(_codec.DecodePayload<EngineRelayRequestFrame>(envelope));

    private static EngineDispatchRequest Req() => new(_agent, "roblox", "g1", "studio-1", "multi_edit", Json("{\"x\":1}"));

    private static async Task<Envelope> NextEventAsync(Conn conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (conn.Events.TryDequeue(out var envelope))
            {
                return envelope;
            }

            await Task.Delay(5, ct);
        }

        throw new OperationCanceledException(ct);
    }

    private static WireRequestContext Answer(Conn from, Guid relayId, string grant = "g1") =>
        new(from, new Envelope(1, EngineRelayWire.RequestType, EnvelopeKind.Request, "r1", null, null, null, null,
            _codec.EncodePayload(EngineRelayFrameMapping.ToFrame(new EngineRelayResultRequest(relayId, new EngineRelayCallResult(grant, true, Json("{\"done\":true}")))))));

    [Fact]
    public async Task A_relay_goes_to_the_owner_desktop_and_its_answer_completes_the_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = new Conn("desktop-1");
        var (broker, handler) = Compose(desktop);

        var call = broker.RelayAsync(Req(), ct);
        var envelope = await NextEventAsync(desktop, ct);

        Assert.Equal(EngineRelayWire.EventType, envelope.T);
        Assert.Equal(EnvelopeKind.Event, envelope.K);
        Assert.Equal(EngineRelayWire.Stream, envelope.S);
        var evt = DecodeEvent(envelope);
        Assert.Equal("multi_edit", evt.Call.Tool);

        var reply = await handler.HandleAsync(Answer(desktop, evt.RelayId), ct);
        Assert.True(reply.IsOk);
        var outcome = await call;
        Assert.Equal(EngineRelayStatus.Completed, outcome.Status);
        Assert.True(outcome.Result!.Payload.GetProperty("done").GetBoolean());
    }

    [Theory]
    [InlineData("human", "windows", true)]
    [InlineData("agent", "windows", true)]
    [InlineData("owner", "android", true)]
    [InlineData("owner", "windows", false)]
    public async Task Only_the_owner_windows_desktop_subscribed_to_the_stream_is_a_target(string kind, string deviceClass, bool subscribed)
    {
        var ct = TestContext.Current.CancellationToken;
        var other = new Conn("d", kind, deviceClass, subscribed);
        var (broker, _) = Compose(other);

        var outcome = await broker.RelayAsync(Req(), ct);

        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Empty(other.Events);
    }

    [Fact]
    public async Task With_several_desktops_the_lowest_device_id_is_chosen()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = new Conn("b-desktop");
        var a = new Conn("a-desktop");
        var (broker, _) = Compose(b, a);

        _ = broker.RelayAsync(Req(), ct);
        await NextEventAsync(a, ct);

        Assert.Empty(b.Events);
    }

    [Fact]
    public async Task A_full_outbound_queue_fails_the_call_as_unavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = new Conn("desktop-1") { QueueFull = true };
        var (broker, _) = Compose(desktop);

        var outcome = await broker.RelayAsync(Req(), ct);

        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task Closing_the_connection_fails_the_pending_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = new Conn("desktop-1");
        var (broker, _) = Compose(desktop);

        var call = broker.RelayAsync(Req(), ct);
        await NextEventAsync(desktop, ct);
        desktop.Close();
        var outcome = await call;

        Assert.Equal(EngineRelayStatus.Unavailable, outcome.Status);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task An_answer_from_another_device_or_an_unknown_id_is_refused_and_the_relay_stays_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = new Conn("desktop-1");
        var intruder = new Conn("desktop-2");
        var (broker, handler) = Compose(desktop);

        var call = broker.RelayAsync(Req(), ct);
        var evt = DecodeEvent(await NextEventAsync(desktop, ct));

        var foreign = await handler.HandleAsync(Answer(intruder, evt.RelayId), ct);
        var unknown = await handler.HandleAsync(Answer(desktop, Guid.NewGuid()), ct);

        Assert.False(foreign.IsOk);
        Assert.False(unknown.IsOk);
        Assert.Equal(1, broker.PendingCount);
        Assert.False(call.IsCompleted);

        Assert.True((await handler.HandleAsync(Answer(desktop, evt.RelayId), ct)).IsOk);
        Assert.Equal(EngineRelayStatus.Completed, (await call).Status);
    }

    [Fact]
    public async Task A_second_answer_for_the_same_relay_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var desktop = new Conn("desktop-1");
        var (broker, handler) = Compose(desktop);

        var call = broker.RelayAsync(Req(), ct);
        var evt = DecodeEvent(await NextEventAsync(desktop, ct));
        Assert.True((await handler.HandleAsync(Answer(desktop, evt.RelayId), ct)).IsOk);
        await call;

        Assert.False((await handler.HandleAsync(Answer(desktop, evt.RelayId), ct)).IsOk);
    }

    [Fact]
    public async Task A_non_target_connection_cannot_answer_and_an_empty_payload_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var human = new Conn("phone", "human", "android");
        var (_, handler) = Compose(human);

        Assert.False((await handler.HandleAsync(Answer(human, Guid.NewGuid()), ct)).IsOk);

        var desktop = new Conn("desktop-1");
        var empty = new WireRequestContext(desktop, new Envelope(1, EngineRelayWire.RequestType, EnvelopeKind.Request, "r2", null, null, null, null, default));
        Assert.False((await handler.HandleAsync(empty, ct)).IsOk);
    }
}
