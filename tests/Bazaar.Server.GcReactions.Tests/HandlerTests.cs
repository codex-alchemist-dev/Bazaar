using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.GcReactions.Abstractions;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Bazaar.Testing;

namespace Bazaar.Server.GcReactions.Tests;

/// <summary>The handlers over in-memory fakes: authorisation, limits, no-op rules, and who receives the live events.</summary>
public class HandlerTests
{
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly Guid _ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid _outsider = Guid.Parse("00000000-0000-7000-8000-00000000000f");
    private static readonly JsonWireCodec _codec = new();

    private readonly FakeClock _clock = new();
    private readonly FakeLog _log = new();
    private readonly FakeAccess _access = new();
    private readonly FakeDirectory _directory = new();
    private readonly GcReactionsOptions _options = new();
    private readonly SlidingRateLimiter _limiter;
    private readonly TypingTracker _tracker;
    private readonly GcLiveBroadcaster _broadcaster;

    public HandlerTests()
    {
        _limiter = new SlidingRateLimiter(_clock, TimeSpan.FromMinutes(1));
        _tracker = new TypingTracker(_clock);
        _broadcaster = new GcLiveBroadcaster(_directory, new SyncOptions(), _clock);
        _access.Members.UnionWith([_ada, _bob]);
        _log.Messages.UnionWith([3L, 4L]);
    }

    private GcReactHandler React() => new(_log, _access, _log, _broadcaster, _limiter, _options);

    private GcReactionsHandler Reactions() => new(_access, _log);

    private GcTypingHandler Typing() => new(_access, _tracker, _broadcaster, _options);

    private static WireRequestContext Context(FakeConnection connection, string type, object payload, string id = "req-1") =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, id, null, null, null, null, _codec.EncodePayload(payload)));

    private static T Decode<T>(WireReply reply) =>
        _codec.DecodePayload<T>(new Envelope(1, "reply", EnvelopeKind.Response, null, null, null, null, null, reply.Payload));

    private FakeConnection Connect(Guid who, bool subscribed = true, string kind = "human")
    {
        var connection = new FakeConnection(new PrincipalRef(who, kind), subscribed ? [WireStreams.Gc(_gc)] : []);
        _directory.All.Add(connection);
        return connection;
    }

    private async Task<WireReply> ReactAsync(FakeConnection who, long target, string emoji, bool add = true, string id = "req-1") =>
        await React().HandleAsync(Context(who, WireTypes.GcReact, new GcReact(_gc, target, emoji, add), id), CancellationToken.None);

    [Fact]
    public async Task A_member_adds_a_reaction_as_one_event_with_the_message_as_its_target()
    {
        var ada = Connect(_ada);

        var reply = await ReactAsync(ada, 3, "👍");

        Assert.True(reply.IsOk);
        Assert.Equal(new GcReactResult(1, true), Decode<GcReactResult>(reply));
        var append = Assert.Single(_log.Appended);
        Assert.Equal((GcEventKinds.Reaction, AuthorKind.Human, _ada, SourceTrust.MemberHuman, 3L, "req-1"),
            (append.Kind, append.AuthorKind, append.AuthorId, append.SourceTrust, append.TargetSeq, append.ClientMsgId));
        Assert.True(ReactionPayload.TryParse(append.PayloadJson!, out var emoji, out var add));
        Assert.Equal(("👍", true), (emoji, add));
    }

    [Fact]
    public async Task An_agent_reaction_is_written_as_agent_authored()
    {
        _access.Members.Add(_bob);
        var agent = Connect(_bob, kind: "agent");

        await ReactAsync(agent, 3, "👍");

        var append = Assert.Single(_log.Appended);
        Assert.Equal((AuthorKind.Agent, SourceTrust.Agent), (append.AuthorKind, append.SourceTrust));
    }

    [Fact]
    public async Task A_non_member_gets_not_found_and_nothing_is_written_or_pushed()
    {
        var ada = Connect(_ada);
        var outsider = Connect(_outsider, subscribed: false);

        var reply = await ReactAsync(outsider, 3, "👍");

        Assert.False(reply.IsOk);
        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Empty(_log.Appended);
        Assert.Empty(ada.Received);
    }

    [Fact]
    public async Task An_unknown_target_is_not_found()
    {
        var reply = await ReactAsync(Connect(_ada), 99, "👍");

        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Empty(_log.Appended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ok")]
    [InlineData("👍👍")]
    public async Task A_bad_emoji_is_a_bad_request_and_writes_nothing(string emoji)
    {
        var reply = await ReactAsync(Connect(_ada), 3, emoji);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Empty(_log.Appended);
    }

    [Fact]
    public async Task A_request_without_an_id_is_refused()
    {
        var reply = await React().HandleAsync(
            Context(Connect(_ada), WireTypes.GcReact, new GcReact(_gc, 3, "👍", true), id: ""), CancellationToken.None);

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task Adding_a_reaction_already_in_effect_writes_nothing_and_answers_with_the_event_in_effect()
    {
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a");

        var again = Decode<GcReactResult>(await ReactAsync(ada, 3, "👍", id: "b"));

        Assert.Equal(new GcReactResult(1, false), again);
        Assert.Single(_log.Appended);
    }

    [Fact]
    public async Task Removing_a_reaction_that_is_not_there_is_a_no_op()
    {
        var reply = Decode<GcReactResult>(await ReactAsync(Connect(_ada), 3, "👍", add: false));

        Assert.Equal(new GcReactResult(0, false), reply);
        Assert.Empty(_log.Appended);
    }

    [Fact]
    public async Task Add_then_remove_keeps_both_events_and_the_folded_state_is_empty()
    {
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a");
        var removed = Decode<GcReactResult>(await ReactAsync(ada, 3, "👍", add: false, id: "b"));

        Assert.Equal(new GcReactResult(2, true), removed);
        Assert.Equal(2, _log.Appended.Count);
        var state = Decode<GcReactionsReply>(await Reactions().HandleAsync(
            Context(ada, WireTypes.GcReactions, new GcReactionsRequest(_gc, null)), CancellationToken.None));
        Assert.Empty(state.Items);
    }

    [Fact]
    public async Task The_live_event_goes_to_every_subscriber_of_the_gc_and_to_nobody_else()
    {
        var ada = Connect(_ada);
        var bob = Connect(_bob);
        var elsewhere = Connect(_outsider, subscribed: false);

        await ReactAsync(ada, 3, "👍");

        foreach (var connection in new[] { ada, bob })
        {
            var frame = Assert.Single(connection.Received);
            Assert.Equal((WireTypes.GcReaction, EnvelopeKind.Event, WireStreams.Gc(_gc), (long?)1), (frame.T, frame.K, frame.S, frame.N));
            var body = _codec.DecodePayload<WireGcReaction>(frame);
            Assert.Equal((_gc, 3L, "👍", _ada, true), (body.GcId, body.TargetN, body.Emoji, body.By, body.Add));
        }

        Assert.Empty(elsewhere.Received);
    }

    [Fact]
    public async Task A_no_op_pushes_nothing()
    {
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a");
        ada.Received.Clear();

        await ReactAsync(ada, 3, "👍", id: "b");

        Assert.Empty(ada.Received);
    }

    [Fact]
    public async Task One_person_may_leave_only_as_many_reactions_as_the_limit_on_a_message()
    {
        _options.MaxPerPersonPerMessage = 2;
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a");
        await ReactAsync(ada, 3, "🎉", id: "b");

        var third = await ReactAsync(ada, 3, "🔥", id: "c");

        Assert.Equal(WireErrorCodes.BadRequest, third.Error!.Code);
        Assert.Equal(2, _log.Appended.Count);
        Assert.True((await ReactAsync(ada, 4, "🔥", id: "d")).IsOk); // another message has its own budget
    }

    [Fact]
    public async Task A_message_holds_only_as_many_different_emoji_as_the_limit_but_joining_an_existing_one_is_fine()
    {
        _options.MaxDistinctPerMessage = 1;
        await ReactAsync(Connect(_ada), 3, "👍", id: "a");
        var bob = Connect(_bob);

        var different = await ReactAsync(bob, 3, "🎉", id: "b");
        var same = await ReactAsync(bob, 3, "👍", id: "c");

        Assert.Equal(WireErrorCodes.BadRequest, different.Error!.Code);
        Assert.True(same.IsOk);
    }

    [Fact]
    public async Task The_rate_limit_refuses_without_writing_and_says_when_to_retry()
    {
        _options.RatePerMinute = 2;
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a");
        await ReactAsync(ada, 3, "🎉", id: "b");

        var third = await ReactAsync(ada, 3, "🔥", id: "c");

        Assert.Equal(WireErrorCodes.RateLimited, third.Error!.Code);
        Assert.Equal(60_000, third.Error.RetryAfterMs);
        Assert.Equal(2, _log.Appended.Count);
    }

    [Fact]
    public async Task Reactions_can_be_turned_off()
    {
        _options.ReactionsEnabled = false;

        var reply = await ReactAsync(Connect(_ada), 3, "👍");

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
        Assert.Empty(_log.Appended);
    }

    [Fact]
    public async Task An_archived_gc_refuses_with_a_bad_request()
    {
        _log.ThrowArchived = true;

        var reply = await ReactAsync(Connect(_ada), 3, "👍");

        Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
    }

    [Fact]
    public async Task The_state_reply_folds_the_log_and_skips_messages_at_or_below_after_n()
    {
        var ada = Connect(_ada);
        var bob = Connect(_bob);
        await ReactAsync(ada, 3, "👍", id: "a");
        await ReactAsync(bob, 3, "👍", id: "b");
        await ReactAsync(bob, 4, "🎉", id: "c");

        var all = Decode<GcReactionsReply>(await Reactions().HandleAsync(
            Context(ada, WireTypes.GcReactions, new GcReactionsRequest(_gc, null)), CancellationToken.None));
        var after = Decode<GcReactionsReply>(await Reactions().HandleAsync(
            Context(ada, WireTypes.GcReactions, new GcReactionsRequest(_gc, 3)), CancellationToken.None));

        Assert.Equal([(3L, "👍", 2), (4L, "🎉", 1)], all.Items.Select(i => (i.TargetN, i.Emoji, i.By.Count)));
        Assert.Equal([(4L, "🎉", 1)], after.Items.Select(i => (i.TargetN, i.Emoji, i.By.Count)));
    }

    [Fact]
    public async Task The_state_reply_is_not_found_for_a_non_reader_and_hides_events_after_a_leavers_last_seq()
    {
        var ada = Connect(_ada);
        await ReactAsync(ada, 3, "👍", id: "a"); // seq 1
        await ReactAsync(ada, 4, "🎉", id: "b"); // seq 2

        var outsider = Connect(_outsider, subscribed: false);
        var denied = await Reactions().HandleAsync(
            Context(outsider, WireTypes.GcReactions, new GcReactionsRequest(_gc, null)), CancellationToken.None);
        _access.ReadThrough[_ada] = 1;
        var limited = Decode<GcReactionsReply>(await Reactions().HandleAsync(
            Context(ada, WireTypes.GcReactions, new GcReactionsRequest(_gc, null)), CancellationToken.None));

        Assert.Equal(WireErrorCodes.NotFound, denied.Error!.Code);
        Assert.Equal([3L], limited.Items.Select(i => i.TargetN));
    }

    private async Task<WireReply> TypeAsync(FakeConnection who, bool typing = true) =>
        await Typing().HandleAsync(Context(who, WireTypes.GcTyping, new GcTyping(_gc, typing)), CancellationToken.None);

    [Fact]
    public async Task Typing_goes_to_the_other_subscribers_as_a_presence_delta_and_is_never_written()
    {
        var ada = Connect(_ada);
        var bob = Connect(_bob);
        var elsewhere = Connect(_outsider, subscribed: false);

        var reply = await TypeAsync(ada);

        Assert.True(Decode<GcTypingResult>(reply).Accepted);
        var frame = Assert.Single(bob.Received);
        Assert.Equal((WireTypes.PresenceUpdate, WireStreams.Presence, (long?)null), (frame.T, frame.S, frame.N));
        Assert.Equal(new PresenceTyping(PresenceKinds.Typing, _gc, _ada, true, 5000), _codec.DecodePayload<PresenceTyping>(frame));
        Assert.Empty(ada.Received); // not echoed to the sender
        Assert.Empty(elsewhere.Received);
        Assert.Empty(_log.Appended);
    }

    [Fact]
    public async Task Typing_from_a_non_member_is_not_found_and_pushes_nothing()
    {
        var bob = Connect(_bob);

        var reply = await TypeAsync(Connect(_outsider, subscribed: false));

        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
        Assert.Empty(bob.Received);
    }

    [Fact]
    public async Task A_refresh_inside_the_minimum_interval_is_dropped_and_a_stop_always_goes_out()
    {
        var ada = Connect(_ada);
        var bob = Connect(_bob);
        await TypeAsync(ada);

        var dropped = Decode<GcTypingResult>(await TypeAsync(ada));
        var stop = Decode<GcTypingResult>(await TypeAsync(ada, typing: false));

        Assert.False(dropped.Accepted);
        Assert.True(stop.Accepted);
        Assert.Equal([true, false], bob.Received.Select(f => _codec.DecodePayload<PresenceTyping>(f).Typing));
    }

    [Fact]
    public async Task A_refresh_after_the_interval_is_taken()
    {
        var ada = Connect(_ada);
        var bob = Connect(_bob);
        await TypeAsync(ada);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(Decode<GcTypingResult>(await TypeAsync(ada)).Accepted);
        Assert.Equal(2, bob.Received.Count);
    }

    [Fact]
    public async Task Typing_can_be_turned_off()
    {
        _options.TypingEnabled = false;
        var bob = Connect(_bob);

        var reply = await TypeAsync(Connect(_ada));

        Assert.False(Decode<GcTypingResult>(reply).Accepted);
        Assert.Empty(bob.Received);
    }

    [Fact]
    public async Task The_ttl_in_the_delta_is_the_operator_setting()
    {
        _options.TypingTtlS = 9;
        var bob = Connect(_bob);

        await TypeAsync(Connect(_ada));

        Assert.Equal(9000, _codec.DecodePayload<PresenceTyping>(bob.Received.Single()).TtlMs);
    }

    // ---- fakes ----

    private sealed class FakeAccess : IGcWriteAccess, IGcReadAccess
    {
        public HashSet<Guid> Members { get; } = [];

        public Dictionary<Guid, long> ReadThrough { get; } = [];

        public Task<HashSet<Guid>> LoadActiveMemberIdsAsync(Guid gcId, CancellationToken ct) =>
            Task.FromResult(gcId == _gc ? new HashSet<Guid>(Members) : []);

        public ValueTask<bool> CanWriteAsync(Guid gcId, GcPrincipal writer, CancellationToken ct) =>
            ValueTask.FromResult(gcId == _gc && Members.Contains(writer.Id));

        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct)
        {
            if (gcId != _gc || !Members.Contains(viewer.Id))
                return ValueTask.FromResult(GcReadGrant.Denied);
            return ValueTask.FromResult(ReadThrough.TryGetValue(viewer.Id, out var through) ? GcReadGrant.Through(through) : GcReadGrant.Full);
        }
    }

    /// <summary>The log writer and the reaction store over one list, so what a handler appends is what the fold reads back.</summary>
    private sealed class FakeLog : IGcLogWriter, IGcReactionStore
    {
        private long _seq;

        public HashSet<long> Messages { get; } = [];

        public List<AppendRequest> Appended { get; } = [];

        public bool ThrowArchived { get; set; }

        private readonly List<ReactionEventRow> _rows = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            if (ThrowArchived)
                throw new GcArchivedException(request.GcId);
            Appended.Add(request);
            var seq = ++_seq;
            if (ReactionPayload.TryParse(request.PayloadJson!, out var emoji, out var add))
                _rows.Add(new ReactionEventRow(seq, request.AuthorId, request.TargetSeq!.Value, emoji, add));
            return Task.FromResult(new AppendResult(request.GcId, seq, Guid.NewGuid(), false, DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> MessageExistsAsync(Guid gcId, long n, CancellationToken ct) => Task.FromResult(Messages.Contains(n));

        public Task<IReadOnlyList<ReactionEventRow>> ReadAsync(Guid gcId, long? targetN, long? afterN, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReactionEventRow>>(
                [.. _rows.Where(r => (targetN is null || r.TargetN == targetN) && (afterN is null || r.TargetN > afterN))]);
    }

    private sealed class FakeDirectory : IWireConnectionDirectory
    {
        public List<FakeConnection> All { get; } = [];

        public IReadOnlyCollection<IWireConnection> Connections => [.. All];
    }

    private sealed class FakeSubscriptions(IReadOnlyCollection<string> streams) : ISubscriptionSession
    {
        public IReadOnlyCollection<string> Streams => streams;

        public Task<SubscribeOutcome> SubscribeAsync(Subscribe request, CancellationToken ct) => throw new NotSupportedException();

        public void Unsubscribe(IEnumerable<string> streams) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class FakeConnection(PrincipalRef principal, IReadOnlyCollection<string> streams) : IWireConnection
    {
        public List<Envelope> Received { get; } = [];

        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal => principal;

        public string DeviceId => "test-device";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public ISubscriptionSession? Subscriptions { get; } = new FakeSubscriptions(streams);

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope)
        {
            Received.Add(envelope);
            return true;
        }

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
