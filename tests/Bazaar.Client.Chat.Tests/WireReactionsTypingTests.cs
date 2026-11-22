using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Contracts.Wire;
using Bazaar.Testing;

namespace Bazaar.Client.Chat.Tests;

/// <summary>The wire-backed reaction and typing sources (docs/design/02, "Reactions on the wire" and "Typing signal").</summary>
public class WireChatReactionSourceTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _me = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _other = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a2");

    private static WireGcReaction Event(long n, string emoji, Guid by, bool add, Guid? gc = null) =>
        new(gc ?? _gc, n, emoji, by, add, DateTimeOffset.UnixEpoch);

    private static WireChatReactionSource Source(Guid? me = null) => new(_gc, () => me ?? _me);

    [Fact]
    public void Add_events_build_chips_with_counts_and_the_own_flag()
    {
        var source = Source();

        source.Apply(Event(3, "👍", _other, true));
        source.Apply(Event(3, "👍", _me, true));
        source.Apply(Event(3, "🎉", _other, true));

        Assert.Equal([new ChatReaction("👍", 2, true), new ChatReaction("🎉", 1, false)], source.Reactions(3));
        Assert.Empty(source.Reactions(4));
    }

    [Fact]
    public void Applying_an_event_twice_changes_nothing_and_raises_nothing()
    {
        var source = Source();
        var raised = new List<long>();
        source.Changed += (_, n) => raised.Add(n);

        source.Apply(Event(1, "👍", _other, true));
        source.Apply(Event(1, "👍", _other, true));
        source.Apply(Event(1, "👎", _other, false));

        Assert.Equal([1L], raised);
        Assert.Equal(1, source.Reactions(1).Single().Count);
    }

    [Fact]
    public void Remove_drops_the_chip_when_the_last_reactor_leaves()
    {
        var source = Source();
        source.Apply(Event(1, "👍", _other, true));
        source.Apply(Event(1, "👍", _me, true));

        source.Apply(Event(1, "👍", _other, false));
        Assert.Equal(new ChatReaction("👍", 1, true), source.Reactions(1).Single());

        source.Apply(Event(1, "👍", _me, false));
        Assert.Empty(source.Reactions(1));
    }

    [Fact]
    public void Events_of_other_group_chats_are_ignored()
    {
        var source = Source();

        source.Apply(Event(1, "👍", _other, true, gc: Guid.NewGuid()));

        Assert.Empty(source.Reactions(1));
    }

    [Fact]
    public void Load_replaces_the_state_and_reports_every_touched_message()
    {
        var source = Source();
        source.Apply(Event(1, "👍", _other, true));
        var raised = new List<long>();
        source.Changed += (_, n) => raised.Add(n);

        source.BeginLoad();
        source.Load(new GcReactionsReply([new WireReactionGroup(2, "🎉", [_me, _other])]));

        Assert.Empty(source.Reactions(1));
        Assert.Equal(new ChatReaction("🎉", 2, true), source.Reactions(2).Single());
        Assert.Equal([1L, 2L], raised.Order());
    }

    [Fact]
    public void Events_that_arrive_while_a_snapshot_loads_are_applied_after_it()
    {
        var source = Source();

        source.BeginLoad();
        source.Apply(Event(2, "🎉", _other, false)); // the remove arrives while the snapshot is in flight
        source.Apply(Event(5, "🔥", _other, true));
        Assert.Empty(source.Reactions(5)); // held, not visible yet
        source.Load(new GcReactionsReply([new WireReactionGroup(2, "🎉", [_other])]));

        Assert.Empty(source.Reactions(2));
        Assert.Equal(new ChatReaction("🔥", 1, false), source.Reactions(5).Single());
    }

    [Fact]
    public void A_failed_load_still_applies_the_held_events()
    {
        var source = Source();

        source.BeginLoad();
        source.Apply(Event(1, "👍", _other, true));
        source.AbortLoad();

        Assert.Equal(1, source.Reactions(1).Single().Count);
    }
}

public class WireChatTypingSourceTests
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _ada = Guid.Parse("0196a6f0-0000-7000-8000-0000000000b1");
    private static readonly Guid _bob = Guid.Parse("0196a6f0-0000-7000-8000-0000000000b2");

    private static PresenceTyping Signal(Guid who, bool typing = true, int ttlMs = 5000, Guid? gc = null) =>
        new(PresenceKinds.Typing, gc ?? _gc, who, typing, ttlMs);

    private static string Name(Guid id) => id == _ada ? "Ada" : "Bob";

    [Fact]
    public void A_signal_shows_the_name_and_expires_after_its_ttl()
    {
        var clock = new FakeClock();
        using var source = new WireChatTypingSource(_gc, Name, clock);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        source.Apply(Signal(_ada));
        Assert.Equal(["Ada"], source.Typers);

        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Equal(["Ada"], source.Typers);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Empty(source.Typers);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_refresh_restarts_the_expiry_without_raising_a_change()
    {
        var clock = new FakeClock();
        using var source = new WireChatTypingSource(_gc, Name, clock);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        source.Apply(Signal(_ada));
        clock.Advance(TimeSpan.FromSeconds(3));
        source.Apply(Signal(_ada));
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(["Ada"], source.Typers);
        Assert.Equal(1, raised);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(source.Typers);
    }

    [Fact]
    public void A_stop_and_a_cleared_person_leave_at_once_and_names_keep_their_start_order()
    {
        var clock = new FakeClock();
        using var source = new WireChatTypingSource(_gc, Name, clock);

        source.Apply(Signal(_bob));
        source.Apply(Signal(_ada));
        Assert.Equal(["Bob", "Ada"], source.Typers);

        source.Apply(Signal(_bob, typing: false));
        Assert.Equal(["Ada"], source.Typers);
        source.Clear(_ada);
        Assert.Empty(source.Typers);
    }

    [Fact]
    public void Signals_of_other_group_chats_are_ignored()
    {
        using var source = new WireChatTypingSource(_gc, Name, new FakeClock());

        source.Apply(Signal(_ada, gc: Guid.NewGuid()));

        Assert.Empty(source.Typers);
    }
}

/// <summary>The session over the fake socket: reactions and typing through the real <see cref="WireClient"/>.</summary>
public class GcChatSessionReactionsTypingTests : IAsyncLifetime
{
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000001");
    private static readonly Guid _other = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a2");
    private static readonly string _stream = WireStreams.Gc(_gc);

    private readonly FakeWireTransport _transport = new();
    private readonly GcChatSession _session;

    public GcChatSessionReactionsTypingTests()
    {
        var client = new WireClient(_transport, _transport.Codec, new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test", Jitter = () => 1.0 },
            new FakeClock());
        _session = new GcChatSession(
            client,
            new ChatConnectionSettings(new Uri("ws://localhost/ws"), "token", _gc, "me"),
            new ChatTimeline(new Dictionary<Guid, ChatMember> { [_other] = new ChatMember("Ada", false) }, "me", TimeZoneInfo.Utc));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Starts the session and, unless told not to, answers the reaction fetch every new connection makes with an empty state.</summary>
    private async Task<FakeConnection> StartAsync(bool answerReactionFetch = true)
    {
        await _session.StartAsync(TestContext.Current.CancellationToken);
        await Until(() => _transport.Count == 1 && _transport.Latest.SentOfType(WireTypes.Subscribe).Any());
        var conn = _transport.Latest;
        if (answerReactionFetch)
        {
            await Until(() => conn.SentOfType(WireTypes.GcReactions).Any());
            Answer(conn, conn.SentOfType(WireTypes.GcReactions).Single(), new GcReactionsReply([]));
            await Until(() => !_session.Reactions.IsLoading);
        }

        return conn;
    }

    private void Answer<T>(FakeConnection conn, Envelope request, T payload) =>
        conn.Push(new Envelope(1, request.T, EnvelopeKind.Response, null, request.Id, null, null, DateTimeOffset.UnixEpoch,
            _transport.Codec.EncodePayload(payload)));

    [Fact]
    public async Task Each_connection_fetches_the_reaction_state_and_loads_it()
    {
        var conn = await StartAsync(answerReactionFetch: false);
        await Until(() => conn.SentOfType(WireTypes.GcReactions).Any());
        var request = conn.SentOfType(WireTypes.GcReactions).Single();
        Assert.Equal(_gc, _transport.Codec.DecodePayload<GcReactionsRequest>(request).GcId);

        Answer(conn, request, new GcReactionsReply([new WireReactionGroup(4, "🎉", [_other])]));

        await Until(() => _session.Reactions.Reactions(4).Count == 1);
        Assert.Equal(new ChatReaction("🎉", 1, false), _session.Reactions.Reactions(4).Single());
    }

    [Fact]
    public async Task A_live_reaction_event_reaches_the_source()
    {
        var conn = await StartAsync();

        conn.PushEvent(WireTypes.GcReaction, new WireGcReaction(_gc, 2, "👍", _other, true, DateTimeOffset.UnixEpoch), _stream, 9);

        await Until(() => _session.Reactions.Reactions(2).Count == 1);
    }

    [Fact]
    public async Task React_sends_gc_react_and_the_local_state_follows_the_answer()
    {
        var conn = await StartAsync();
        var react = _session.ReactAsync(6, "👍", add: true, TestContext.Current.CancellationToken);
        await Until(() => conn.SentOfType(WireTypes.GcReact).Any());
        var request = conn.SentOfType(WireTypes.GcReact).Single();
        Assert.Equal(new GcReact(_gc, 6, "👍", true), _transport.Codec.DecodePayload<GcReact>(request));

        Answer(conn, request, new GcReactResult(11, true));
        await react;

        // The welcome carries a principal id, so the chip is the client's own.
        Assert.Equal(new ChatReaction("👍", 1, true), _session.Reactions.Reactions(6).Single());
    }

    [Fact]
    public async Task Toggle_removes_a_reaction_that_is_already_the_clients_own()
    {
        var conn = await StartAsync();
        var first = _session.ReactAsync(6, "👍", add: true, TestContext.Current.CancellationToken);
        await Until(() => conn.SentOfType(WireTypes.GcReact).Any());
        Answer(conn, conn.SentOfType(WireTypes.GcReact).Single(), new GcReactResult(11, true));
        await first;

        var toggle = _session.ToggleReactionAsync(6, "👍", TestContext.Current.CancellationToken);
        await Until(() => conn.SentOfType(WireTypes.GcReact).Count() == 2);
        var request = conn.SentOfType(WireTypes.GcReact).Last();
        Assert.False(_transport.Codec.DecodePayload<GcReact>(request).Add);
        Answer(conn, request, new GcReactResult(12, true));
        await toggle;

        Assert.Empty(_session.Reactions.Reactions(6));
    }

    [Fact]
    public async Task A_typing_delta_shows_the_name_and_the_typers_message_clears_it()
    {
        var conn = await StartAsync();

        conn.PushEvent(WireTypes.PresenceUpdate, new PresenceTyping(PresenceKinds.Typing, _gc, _other, true, 5000), WireStreams.Presence);
        await Until(() => _session.Typing.Typers.Count == 1);
        Assert.Equal(["Ada"], _session.Typing.Typers);

        conn.PushEvent(WireTypes.GcMessage,
            new WireGcMessage(_gc, 1, 1, Guid.NewGuid(), _other, "done", DateTimeOffset.UnixEpoch, false, [], null), _stream, 1);
        await Until(() => _session.Typing.Typers.Count == 0);
    }

    [Fact]
    public async Task Typing_in_the_draft_box_sends_a_start_and_clearing_it_sends_a_stop()
    {
        var conn = await StartAsync();
        var model = new ChatViewModel(_session, null, _session.Reactions, _session.Typing);

        model.Draft = "hel";
        await Until(() => conn.SentOfType(WireTypes.GcTyping).Count() == 1);
        Assert.True(_transport.Codec.DecodePayload<GcTyping>(conn.SentOfType(WireTypes.GcTyping).Single()).Typing);

        model.Draft = "hello";
        model.Draft = string.Empty;
        await Until(() => conn.SentOfType(WireTypes.GcTyping).Count() == 2);

        Assert.False(_transport.Codec.DecodePayload<GcTyping>(conn.SentOfType(WireTypes.GcTyping).Last()).Typing);
    }

    [Fact]
    public async Task Typing_is_sent_once_per_refresh_window_and_a_stop_once()
    {
        var conn = await StartAsync();

        var first = _session.NotifyTypingAsync(true, TestContext.Current.CancellationToken);
        await Until(() => conn.SentOfType(WireTypes.GcTyping).Any());
        Answer(conn, conn.SentOfType(WireTypes.GcTyping).Single(), new GcTypingResult(true));
        await first;

        await _session.NotifyTypingAsync(true, TestContext.Current.CancellationToken); // inside the refresh window: not sent
        Assert.Single(conn.SentOfType(WireTypes.GcTyping));

        var stop = _session.NotifyTypingAsync(false, TestContext.Current.CancellationToken);
        await Until(() => conn.SentOfType(WireTypes.GcTyping).Count() == 2);
        var request = conn.SentOfType(WireTypes.GcTyping).Last();
        Assert.False(_transport.Codec.DecodePayload<GcTyping>(request).Typing);
        Answer(conn, request, new GcTypingResult(true));
        await stop;

        await _session.NotifyTypingAsync(false, TestContext.Current.CancellationToken); // already stopped: not sent
        Assert.Equal(2, conn.SentOfType(WireTypes.GcTyping).Count());
    }
}
