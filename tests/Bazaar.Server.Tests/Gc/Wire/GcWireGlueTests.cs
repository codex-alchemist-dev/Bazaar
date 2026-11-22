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
            new GcLogWriter(connection.Data, new RecordingNotifier()), new PlaintextGcBodyCodec(), new GcReadAccess(connection.Data),
            new Bazaar.Server.Gc.Mentions.InboxRowWriter(connection.Data));
        return await handler.HandleAsync(
            Context(connection, WireTypes.GcSend, requestId, new GcSend(gc, text, mentions, null)), ct);
    }

    [Fact]
    public async Task A_human_mention_of_an_agent_writes_an_inbox_row_that_wakes_it()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var agentId = Guid.CreateVersion7();
        var gc = await GcWithMemberAsync(member);
        await AddMemberAsync(gc, agentId);
        await using (var agent = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'mention wake test', 'active')"))
        {
            agent.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
            await agent.ExecuteNonQueryAsync(ct);
        }
        var connection = new FakeWireConnection(db.DataSource, member);

        var reply = await SendAsync(connection, gc, "@agent please look", "mention-1", [agentId], ct);

        Assert.True(reply.IsOk);
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM inbox_item WHERE agent_id = $1 AND gc_id = $2", agentId, gc));
    }

    private static WireRequestContext RawContext(FakeWireConnection connection, string type, string requestId, string json) =>
        new(connection, new Envelope(1, type, EnvelopeKind.Request, requestId, null, null, null, null,
            System.Text.Encoding.UTF8.GetBytes(json)));

    // Chaos round 3: a message is stored and returned exactly, or refused with a designed error; never an internal fault and
    // never a silently changed text. NUL cannot reach Postgres text, and a lone surrogate cannot be UTF-8.
    [Theory]
    [InlineData("a\\u0000b")]
    [InlineData("\\ud800")]
    [InlineData("x\\udc00y")]
    [InlineData("hi \\ud83d")]
    [InlineData("\\u202eevil\\u200b")]
    public async Task Hostile_text_is_stored_exactly_or_refused_with_bad_request(string escaped)
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);
        var handler = new GcSendHandler(
            new GcLogWriter(connection.Data, new RecordingNotifier()), new PlaintextGcBodyCodec(), new GcReadAccess(connection.Data),
            new Bazaar.Server.Gc.Mentions.InboxRowWriter(connection.Data));

        WireReply reply;
        try
        {
            reply = await handler.HandleAsync(RawContext(connection, WireTypes.GcSend, "hostile", $"{{\"gcId\":\"{gc}\",\"text\":\"{escaped}\"}}"), ct);
        }
        catch (WireFormatException)
        {
            return; // The connection answers a malformed payload with bad_request.
        }

        if (!reply.IsOk)
        {
            Assert.Equal(WireErrorCodes.BadRequest, reply.Error!.Code);
            return;
        }
        var expected = System.Text.Json.JsonSerializer.Deserialize<string>($"\"{escaped}\"");
        var item = Assert.Single(await Source.ReadAsync(gc, 0, 0, 0, 10, ct));
        Assert.Equal(expected, item.Text);
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
    public async Task A_message_over_the_body_cap_in_bytes_is_refused_before_anything_is_stored()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var connection = new FakeWireConnection(db.DataSource, member);

        // doc 01: gc.message_max_bytes is 32768 bytes. 20,000 three-byte characters are under the character limit and over
        // the byte cap; an edit of the same text is already refused, so a send must be too.
        var text = new string('\u20ac', 20_000);
        var refused = await SendAsync(connection, gc, text, "big-1", ct: ct);
        var atCap = await SendAsync(connection, gc, new string('a', 32768), "cap-1", ct: ct);

        Assert.False(refused.IsOk);
        Assert.Equal(WireErrorCodes.BadRequest, refused.Error!.Code);
        Assert.True(atCap.IsOk);
        Assert.Equal(1, (await Source.GetHeadAsync(gc, ct))!.Value.LatestN);
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
    public async Task A_replay_page_for_a_cursor_near_the_head_does_not_cost_the_whole_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        const int total = 400_000;
        // Raw rows for a long-lived GC, with one edit near the head. The page below is what the dispatcher reads after each
        // commit and what a subscribe with a near-head cursor reads: it must cost the new entries, not every message ever sent.
        await using (var seed = db.DataSource.CreateCommand("""
            INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust, message_id, version)
              SELECT @gc, s, 'message', 'human', @author, 'member-human', md5(@gc::text || s)::uuid, 1 FROM generate_series(1, @total) s;
            INSERT INTO gc_message (gc_id, message_id, first_seq, author_id)
              SELECT @gc, md5(@gc::text || s)::uuid, s, @author FROM generate_series(1, @total) s;
            INSERT INTO gc_message_version (gc_id, message_id, version, seq, body_ct, body_nonce, editor_id)
              SELECT @gc, md5(@gc::text || s)::uuid, 1, s, '\x78'::bytea, '\x6e'::bytea, @author FROM generate_series(1, @total) s;
            UPDATE gc SET head_seq = @total WHERE gc_id = @gc;
            ANALYZE gc_event;
            ANALYZE gc_message;
            ANALYZE gc_message_version;
            """))
        {
            seed.Parameters.AddWithValue("gc", gc);
            seed.Parameters.AddWithValue("author", member.Id);
            seed.Parameters.AddWithValue("total", total);
            await seed.ExecuteNonQueryAsync(ct);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var page = await Source.ReadAsync(gc, total - 5, total - 5, total - 5, 200, ct);
        watch.Stop();

        Assert.Equal(Enumerable.Range(total - 4, 5).Select(i => (long)i), page.Select(m => m.N));
        // Before the fix this took about 4 s per 200,000 messages (a current-version lookup for every message in the GC).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.0), $"A near-head replay page took {watch.Elapsed}.");
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

        Assert.False(await policy.CanReadAsync(former, PrincipalKind.Human, gc, ct));
    }

    [Fact]
    public async Task The_owner_reads_a_DM_on_the_live_stream_without_membership()
    {
        // Drift audit item 4 (docs/design/01): the owner reads all, DMs included. The owner is not a member of this DM.
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var gc = Guid.CreateVersion7();
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'dm', 'dm', $2)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = other });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AddMemberAsync(gc, other, joinedSeq: 0, leftSeq: null);
        var policy = new GcStreamAccessPolicy(Access);

        Assert.True(await policy.CanReadAsync(owner, PrincipalKind.Owner, gc, ct));
        Assert.False(await policy.CanReadAsync(owner, PrincipalKind.Human, gc, ct));
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
        using var session = engine.CreateSession(member.Id, PrincipalKind.Human, _codec, outbound);

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

    // Scaled-down form of the chaos ramp (docs/design/46 section 17.1): concurrent writers on one GC, live subscribers, subscribers that
    // drop their connection and resume from their cursor, and one that joins late. Every one of them must see seq 1..N once, in order.
    [Fact]
    public async Task Concurrent_writers_and_reconnecting_subscribers_see_every_message_exactly_once_and_in_order()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(member);
        var notifications = new ChannelGcNotifications();
        var writer = new GcLogWriter(db.DataSource, notifications);
        var source = Source;
        var dispatcher = new GcDispatcher(source, new SyncOptions());
        var engine = new SubscriptionEngine(source, new GcStreamAccessPolicy(Access), dispatcher, new SyncOptions());
        using var running = new CancellationTokenSource();
        var dispatching = dispatcher.RunAsync(notifications, null, running.Token);
        const int writers = 6;
        const int perWriter = 20;
        const int total = writers * perWriter;

        async Task<List<(long N, long Ev)>> CollectAsync(bool reconnecting)
        {
            var log = new List<(long N, long Ev)>();
            while (log.Count < total)
            {
                // One loop pass is one connection: its own queue and session, dropped without a goodbye.
                using var outbound = new BoundedOutbound(10_000);
                using var session = engine.CreateSession(member.Id, PrincipalKind.Human, _codec, outbound);
                var cursor = log.Count == 0 ? (N: 0L, Ev: 0L) : log[^1];
                var outcome = await session.SubscribeAsync(new Subscribe([new StreamCursor(WireStreams.Gc(gc), cursor.N, cursor.Ev)]), ct);
                Assert.Equal(SubscribeStatuses.Ok, outcome.Result.Results.Single().Status);
                _ = outcome.StartAsync();
                var taken = 0;
                await foreach (var envelope in outbound.ReadAllAsync(ct))
                {
                    if (envelope.T != WireTypes.GcMessage)
                        continue;
                    var message = _codec.DecodePayload<WireGcMessage>(envelope);
                    log.Add((message.N, message.Ev));
                    if (log.Count == total || (reconnecting && ++taken == 25))
                        break;
                }
            }

            return log;
        }

        var early = new[] { CollectAsync(false), CollectAsync(false), CollectAsync(true), CollectAsync(true) }
            .Select(t => t.WaitAsync(TimeSpan.FromSeconds(60), ct)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
                await writer.AppendAsync(TextAppend(gc, member, $"w{w}-{i}", $"w{w}-{i}"), ct);
        }, ct)));
        var late = await CollectAsync(false).WaitAsync(TimeSpan.FromSeconds(60), ct);
        var logs = (await Task.WhenAll(early)).Append(late).ToList();

        var expected = Enumerable.Range(1, total).Select(n => ((long)n, (long)n)).ToList();
        foreach (var log in logs)
            Assert.Equal(expected, log);
        Assert.Equal((long)total, await db.ScalarAsync<long>("SELECT count(DISTINCT seq) FROM gc_event WHERE gc_id = $1 AND seq BETWEEN 1 AND $2", gc, (long)total));
        running.Cancel();
        await Task.WhenAny(dispatching, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static async Task<WireReply> EditAsync(FakeWireConnection connection, Guid gc, Guid messageId, string text, string requestId)
    {
        var handler = new GcEditHandler(
            new GcLogWriter(connection.Data, new RecordingNotifier()), new PlaintextGcBodyCodec(), new GcReadAccess(connection.Data),
            new Bazaar.Server.Gc.Edit.PgGcEditSource(connection.Data), SettingsFor());
        return await handler.HandleAsync(
            Context(connection, WireTypes.GcEdit, requestId, new GcEdit(gc, messageId, text)), TestContext.Current.CancellationToken);
    }

    private async Task<Guid> MessageIdAtSeqAsync(Guid gc, long seq)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT message_id FROM gc_event WHERE gc_id = $1 AND seq = $2");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_author_edit_adds_version_two_and_the_message_reads_back_with_the_new_text()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var author = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(author);
        var connection = new FakeWireConnection(db.DataSource, author);
        var sent = Decode<GcSendResult>(await SendAsync(connection, gc, "first draft", "send-1", ct: ct));
        var messageId = await MessageIdAtSeqAsync(gc, sent.N);

        var reply = await EditAsync(connection, gc, messageId, "second draft", "edit-1");

        Assert.True(reply.IsOk);
        var edited = Decode<GcSendResult>(reply);
        Assert.Equal(2, edited.Ev);
        await using var versions = db.DataSource.CreateCommand("SELECT COUNT(*) FROM gc_message_version WHERE gc_id = $1 AND message_id = $2");
        versions.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        versions.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        Assert.Equal(2L, await versions.ExecuteScalarAsync(ct));
    }

    // Chaos finding (crash-only campaign): the host was killed after an edit committed but before its answer reached the client. The
    // in-memory request-id cache died with the process, so the client's retry with the same request id was planned again, compared with
    // its own committed text, and refused as "The text is unchanged". The handler is called directly here, as after a restart.
    [Fact]
    public async Task Retrying_a_committed_edit_with_the_same_request_id_returns_the_original_result()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var author = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(author);
        var connection = new FakeWireConnection(db.DataSource, author);
        var sent = Decode<GcSendResult>(await SendAsync(connection, gc, "first draft", "send-r1", ct: ct));
        var messageId = await MessageIdAtSeqAsync(gc, sent.N);

        var first = Decode<GcSendResult>(await EditAsync(connection, gc, messageId, "second draft", "edit-r1"));
        var later = Decode<GcSendResult>(await EditAsync(connection, gc, messageId, "third draft", "edit-r2"));
        var retry = await EditAsync(connection, gc, messageId, "second draft", "edit-r1");

        Assert.True(retry.IsOk);
        Assert.Equal(first, Decode<GcSendResult>(retry));
        Assert.Equal(3, later.Ev);
        await using var versions = db.DataSource.CreateCommand("SELECT COUNT(*) FROM gc_message_version WHERE gc_id = $1 AND message_id = $2");
        versions.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        versions.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        Assert.Equal(3L, await versions.ExecuteScalarAsync(ct));
        await using var head = db.DataSource.CreateCommand("SELECT head_seq FROM gc WHERE gc_id = $1");
        head.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        Assert.Equal(later.N, await head.ExecuteScalarAsync(ct));
    }

    // Chaos finding (crash-only campaign): two edits of one message planned against the same current version both chose the next
    // version number; the second write hit the version key and the client got "internal" with an error-level stack in the log.
    [Fact]
    public async Task Concurrent_edits_of_one_message_all_land_with_contiguous_versions()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var author = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(author);
        var connection = new FakeWireConnection(db.DataSource, author);
        var sent = Decode<GcSendResult>(await SendAsync(connection, gc, "draft 0", "send-c1", ct: ct));
        var messageId = await MessageIdAtSeqAsync(gc, sent.N);

        var replies = await Task.WhenAll(Enumerable.Range(1, 3).Select(i => Task.Run(() => EditAsync(connection, gc, messageId, $"draft {i}", $"edit-c{i}"), ct)));

        Assert.All(replies, r => Assert.True(r.IsOk));
        Assert.Equal([2L, 3L, 4L], replies.Select(r => Decode<GcSendResult>(r).Ev).Order().ToArray());
    }

    [Fact]
    public async Task Someone_who_is_not_the_author_cannot_edit_the_message()
    {
        GcTestSkip.WithoutDatabase();
        var author = Human(Guid.CreateVersion7());
        var other = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(author);
        await AddMemberAsync(gc, other.Id);
        var ct = TestContext.Current.CancellationToken;
        var sent = Decode<GcSendResult>(await SendAsync(new FakeWireConnection(db.DataSource, author), gc, "mine", "send-2", ct: ct));
        var messageId = await MessageIdAtSeqAsync(gc, sent.N);

        var reply = await EditAsync(new FakeWireConnection(db.DataSource, other), gc, messageId, "not mine", "edit-2");

        Assert.False(reply.IsOk);
    }

    // Defaults only: the registry's own defaults (any member may invite; edits are unlimited), as an operator who set nothing sees.
    private static Bazaar.Server.Tests.Settings.InMemorySettingStore SettingsFor() =>
        new Bazaar.Server.Tests.Settings.InMemorySettingStore(
            Bazaar.Server.Settings.SettingRegistry.Known, new Bazaar.Server.Settings.InMemorySettingAuditSink());

    private static async Task<WireReply> AddMemberAsync(FakeWireConnection connection, Guid gc, Guid newMember, string requestId)
    {
        var handler = new GcMemberAddHandler(
            new GcLogWriter(connection.Data, new RecordingNotifier()), new GcReadAccess(connection.Data),
            new Bazaar.Server.Gc.Members.PgGcMembershipSource(connection.Data), SettingsFor());
        return await handler.HandleAsync(
            Context(connection, WireTypes.GcMemberAdd, requestId, new GcMemberAdd(gc, newMember, PrincipalKind.Human, "member")),
            TestContext.Current.CancellationToken);
    }

    private static async Task<WireReply> LeaveAsync(FakeWireConnection connection, Guid gc, Guid member, string requestId)
    {
        var handler = new GcMemberRemoveHandler(
            new GcLogWriter(connection.Data, new RecordingNotifier()), new GcReadAccess(connection.Data),
            new Bazaar.Server.Gc.Members.PgGcMembershipSource(connection.Data));
        return await handler.HandleAsync(
            Context(connection, WireTypes.GcMemberRemove, requestId, new GcMemberRemove(gc, member)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_member_can_add_another_person_and_that_person_becomes_an_active_member()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var author = Human(Guid.CreateVersion7());
        var invitee = Guid.CreateVersion7();
        var gc = await GcWithMemberAsync(author);

        var reply = await AddMemberAsync(new FakeWireConnection(db.DataSource, author), gc, invitee, "add-1");

        Assert.True(reply.IsOk);
        var added = Decode<GcSendResult>(reply);
        await using var row = db.DataSource.CreateCommand(
            "SELECT joined_seq, left_seq, role FROM gc_member_current WHERE gc_id = $1 AND principal_id = $2");
        row.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        row.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitee });
        await using var reader = await row.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(added.N, reader.GetInt64(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal("member", reader.GetString(2));
    }

    [Fact]
    public async Task A_member_who_leaves_gets_a_removal_row_with_left_seq_set()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var leaver = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(leaver);

        var reply = await LeaveAsync(new FakeWireConnection(db.DataSource, leaver), gc, leaver.Id, "leave-1");

        Assert.True(reply.IsOk);
        var left = Decode<GcSendResult>(reply);
        await using var row = db.DataSource.CreateCommand(
            "SELECT left_seq FROM gc_member_current WHERE gc_id = $1 AND principal_id = $2");
        row.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        row.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = leaver.Id });
        Assert.Equal(left.N, await row.ExecuteScalarAsync(ct));
    }

    // Chaos round 3 (access): the stream re-check existed but nothing called it, so a removed member kept receiving a GC's live events
    // on a socket that was open when they left (doc 02: access is rechecked on subscribe and on membership change).
    [Fact]
    public async Task A_member_who_leaves_loses_their_open_stream_and_gets_no_later_message()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var leaver = Human(Guid.CreateVersion7());
        var stayer = Human(Guid.CreateVersion7());
        var gc = await GcWithMemberAsync(leaver);
        await AddMemberAsync(gc, stayer.Id);
        var notifications = new ChannelGcNotifications();
        var writer = new GcLogWriter(db.DataSource, notifications);
        var source = Source;
        var dispatcher = new GcDispatcher(source, new SyncOptions());
        var streamAccess = new GcStreamAccessPolicy(Access);
        var engine = new SubscriptionEngine(source, streamAccess, dispatcher, new SyncOptions());
        using var running = new CancellationTokenSource();
        var dispatching = dispatcher.RunAsync(notifications, null, running.Token);
        var outbound = new BoundedOutbound(1000);
        using var session = engine.CreateSession(leaver.Id, PrincipalKind.Human, _codec, outbound);
        var outcome = await session.SubscribeAsync(new Subscribe([new StreamCursor(WireStreams.Gc(gc), 0, 0)]), ct);
        Assert.Equal(SubscribeStatuses.Ok, outcome.Result.Results.Single().Status);
        _ = outcome.StartAsync();

        var leave = new GcMemberRemoveHandler(
            writer, new GcReadAccess(db.DataSource), new Bazaar.Server.Gc.Members.PgGcMembershipSource(db.DataSource), dispatcher, streamAccess);
        var reply = await leave.HandleAsync(
            Context(new FakeWireConnection(db.DataSource, leaver), WireTypes.GcMemberRemove, "leave-live", new GcMemberRemove(gc, leaver.Id)), ct);
        Assert.True(reply.IsOk);
        await writer.AppendAsync(TextAppend(gc, stayer, "said after the leave", "after-leave"), ct);

        var seen = new List<string>();
        var texts = new List<string>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var envelope in outbound.ReadAllAsync(window.Token))
        {
            seen.Add(envelope.T);
            if (envelope.T == WireTypes.GcMessage)
                texts.Add(_codec.DecodePayload<WireGcMessage>(envelope).Text);
            if (envelope.T == WireTypes.StreamRevoked)
                break;
        }

        Assert.Contains(WireTypes.StreamRevoked, seen);
        Assert.DoesNotContain("said after the leave", texts);
        running.Cancel();
        await Task.WhenAny(dispatching, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private static AppendRequest TextAppend(Guid gc, PrincipalRef author, string text, string clientMsgId) =>
        new(gc, GcEventKinds.Message, AuthorKind.Human, author.Id, SourceTrust.MemberHuman, clientMsgId)
        {
            // Plaintext sealing never awaits, so blocking on the value here is safe in a synchronous helper.
            Message = new PlaintextGcBodyCodec().SealAsync(author.Id, text, default).AsTask().GetAwaiter().GetResult(),
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
