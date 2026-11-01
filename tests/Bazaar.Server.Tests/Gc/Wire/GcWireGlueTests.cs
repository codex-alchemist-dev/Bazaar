using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Wire;

/// <summary>The Stage 3 GLUE: wire handlers and stream reads over the real GC log, on Postgres.</summary>
public sealed class GcWireGlueTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly JsonWireCodec _codec = new();
    private static readonly PrincipalRef _owner = new(Guid.Parse("00000000-0000-7000-8000-0000000000aa"), "owner");

    private static PrincipalRef Human(Guid id) => new(id, "human");

    private static PrincipalRef Agent(Guid id) => new(id, "agent");

    private PgGcStreamSource Source => new(db.DataSource, new PlaintextGcBodyCodec());

    private GcReadAccess Access => new(db.DataSource);

    private async Task AddMemberAsync(Guid gc, Guid principal, long joinedSeq = 0, long? leftSeq = null)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, 'member', $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = joinedSeq });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)leftSeq ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> GcWithMemberAsync(PrincipalRef member)
    {
        var gc = await db.CreateGcAsync();
        await AddMemberAsync(gc, member.Id);
        return gc;
    }

    private static WireRequestContext Context(FakeWireConnection connection, string type, string requestId, object payload) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, requestId, null, null, null, null,
            connection.Codec.EncodePayload(payload)));

    private static T Decode<T>(WireReply reply) =>
        _codec.DecodePayload<T>(new Envelope(1, "reply", EnvelopeKind.Response, null, null, null, null, null, reply.Payload));

    private static async Task<WireReply> SendAsync(FakeWireConnection connection, Guid gc, string text, string requestId = "req-1",
        IReadOnlyList<Guid>? mentions = null, CancellationToken ct = default)
    {
        var handler = new GcSendHandler(
            new GcLogWriter(connection.Data, new RecordingNotifier()), new PlaintextGcBodyCodec(), new GcReadAccess(connection.Data));
        return await handler.HandleAsync(
            Context(connection, WireTypes.GcSend, requestId, new GcSend(gc, text, mentions, null)), ct);
    }

    [Fact]
    public async Task Send_appends_the_message_and_the_stream_reads_it_back()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);

        var reply = await SendAsync(connection, gc, "hello", ct: ct);
        var sent = Decode<GcSendResult>(reply);

        Assert.True(reply.IsOk);
        Assert.Equal(new GcSendResult(1, 1), sent);
        var items = await Source.ReadAsync(gc, after: 0, editAfter: 0, afterEv: 0, limit: 10, ct);
        var message = Assert.Single(items);
        Assert.Equal(("hello", 1L, 1L, member.Id), (message.Text, message.N, message.Ev, message.AuthorId));
        Assert.False(message.Edited);
    }

    [Fact]
    public async Task Repeating_the_request_id_returns_the_original_sequence()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);

        var first = Decode<GcSendResult>(await SendAsync(connection, gc, "once", "same-id", ct: ct));
        var again = Decode<GcSendResult>(await SendAsync(connection, gc, "once", "same-id", ct: ct));

        Assert.Equal(first, again);
        Assert.Equal(new GcHead(1, 1), await Source.GetHeadAsync(gc, ct));
    }

    [Fact]
    public async Task A_non_member_cannot_send_and_gets_not_found()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await GcWithMemberAsync(Human(Guid.CreateVersion7()));
        var outsider = new FakeWireConnection(db.DataSource, Human(Guid.CreateVersion7()));

        var reply = await SendAsync(outsider, gc, "let me in", ct: ct);

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Empty(await Source.ReadAsync(gc, 0, 0, 0, 10, ct));
    }

    [Fact]
    public async Task A_mention_of_a_non_member_is_rejected_before_anything_is_written()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);

        var reply = await SendAsync(connection, gc, "hey @stranger", mentions: [Guid.CreateVersion7()], ct: ct);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Empty(await Source.ReadAsync(gc, 0, 0, 0, 10, ct));
    }

    [Fact]
    public async Task Head_and_history_page_back_through_older_entries()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);
        for (var i = 1; i <= 3; i++)
            await SendAsync(connection, gc, $"m{i}", $"req-{i}", ct: ct);

        var head = await Source.GetHeadAsync(gc, ct);
        var reply = await new GcHistoryHandler(Source, Access).HandleAsync(
            Context(connection, WireTypes.GcHistory, "h-1", new GcHistory(gc, Before: 3, Limit: 1)), ct);
        var page = Decode<GcPage>(reply);

        Assert.Equal(new GcHead(3, 3), head);
        Assert.True(page.HasMore);
        var only = Assert.Single(page.Items);
        Assert.Equal(("m2", 2L), (only.Text, only.N));
    }

    [Fact]
    public async Task A_former_member_loses_the_live_stream()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var former = Guid.CreateVersion7();
        var gc = await db.CreateGcAsync();
        await AddMemberAsync(gc, former, joinedSeq: 0, leftSeq: 2);
        var policy = new GcStreamAccessPolicy(Access);

        Assert.False(await policy.CanReadAsync(former, gc, ct));
    }

    [Fact]
    public async Task Unread_counts_messages_after_the_viewers_read_marker()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);
        for (var i = 1; i <= 3; i++)
            await SendAsync(connection, gc, $"m{i}", $"req-{i}", ct: ct);
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO read_state (principal_id, gc_id, last_read_seq) VALUES ($1, $2, 1)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = member.Id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(2, await Source.CountUnreadAsync(gc, member.Id, ct));
    }

    [Fact]
    public async Task Subscribe_replays_what_the_client_lacks_and_then_streams_new_messages_live()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var notifications = new ChannelGcNotifications();
        var writer = new GcLogWriter(db.DataSource, notifications);
        await writer.AppendAsync(TextAppend(gc, member, "before subscribing", "a"), ct);

        var source = Source;
        var dispatcher = new GcDispatcher(source, new SyncOptions());
        var engine = new SubscriptionEngine(source, new GcStreamAccessPolicy(Access), dispatcher, new SyncOptions());
        using var running = new CancellationTokenSource();
        var dispatching = dispatcher.RunAsync(notifications, null, running.Token);
        var outbound = new BoundedOutbound(1000);
        using var session = engine.CreateSession(member.Id, _codec, outbound);

        var outcome = await session.SubscribeAsync(new Subscribe([new StreamCursor(WireStreams.Gc(gc), 0, 0)]), ct);
        Assert.Equal(SubscribeStatuses.Ok, outcome.Result.Results.Single().Status);
        _ = outcome.StartAsync();

        await writer.AppendAsync(TextAppend(gc, member, "after subscribing", "b"), ct);

        var texts = new List<string>();
        await foreach (var envelope in outbound.ReadAllAsync(ct))
        {
            if (envelope.T != WireTypes.GcMessage)
                continue;
            texts.Add(_codec.DecodePayload<WireGcMessage>(envelope).Text);
            if (texts.Count == 2)
                break;
        }

        Assert.Equal(["before subscribing", "after subscribing"], texts);
        running.Cancel();
        await Task.WhenAny(dispatching, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static AppendRequest TextAppend(Guid gc, PrincipalRef author, string text, string clientMsgId) =>
        new(gc, GcEventKinds.Message, AuthorKind.Human, author.Id, SourceTrust.MemberHuman, clientMsgId)
        {
            Message = new PlaintextGcBodyCodec().Seal(text),
        };

    /// <summary>A fake connection: enough of <see cref="IWireConnection"/> for the handlers and the subscription session.</summary>
    private sealed class FakeWireConnection(NpgsqlDataSource data, PrincipalRef principal) : IWireConnection
    {
        public NpgsqlDataSource Data { get; } = data;

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal => principal;

        public string DeviceId => "test-device";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public SubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }

    /// <summary>Records notifications for the dispatcher, so a test can push commits through it without a listener.</summary>
    private sealed class ChannelGcNotifications : IGcNotifier, IGcNotificationSource
    {
        private readonly Channel<GcNotification> _channel = Channel.CreateUnbounded<GcNotification>();

        public void Notify(Guid gcId, long seq) => _channel.Writer.TryWrite(new GcNotification(gcId, seq));

        public async IAsyncEnumerable<GcNotification> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                while (_channel.Reader.TryRead(out var note))
                    yield return note;
            }
        }
    }
}
