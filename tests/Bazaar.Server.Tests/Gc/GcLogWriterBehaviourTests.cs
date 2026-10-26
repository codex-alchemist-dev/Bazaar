using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>Behaviour of <see cref="GcLogWriter"/> against a real database (docs/design/01, 04 "GC append").</summary>
public sealed class GcLogWriterBehaviourTests(GcDb db) : IClassFixture<GcDb>
{
    private sealed class RecordingNotifier : IGcNotifier
    {
        public List<(Guid GcId, long Seq)> Calls { get; } = [];

        public void Notify(Guid gcId, long seq) => Calls.Add((gcId, seq));
    }

    private static MessageBody Body(string text) =>
        new(Encoding.UTF8.GetBytes(text), Guid.CreateVersion7());

    private static AppendRequest Message(Guid gc, Guid author, string key, string text = "hello", IReadOnlyList<Guid>? mentions = null) =>
        new(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, key)
        {
            Message = Body(text),
            Mentions = mentions ?? [],
        };

    private static AppendRequest Structured(Guid gc, Guid author, string key) =>
        new(gc, GcEventKinds.Structured, AuthorKind.Agent, author, SourceTrust.Agent, key);

    private static string Wire<T>(T value) where T : struct, Enum => JsonSerializer.Serialize(value).Trim('"');

    // A fresh append reports the writer's clock (100 ns ticks) while a repeat reads timestamptz (microseconds), so the
    // same event can report two CreatedAt values that differ below a microsecond. Compare at the stored precision.
    private static DateTimeOffset StoredPrecision(DateTimeOffset t) => new(t.Ticks - t.Ticks % 10, t.Offset);

    private async Task<List<object[]>> RowsAsync(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private async Task ArchiveAsync(Guid gc)
    {
        await using var cmd = db.DataSource.CreateCommand("UPDATE gc SET archived_at = now() WHERE gc_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertNothingWrittenAsync(Guid gc)
    {
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task Message_append_writes_event_message_and_first_version()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);
        var keyId = Guid.CreateVersion7();
        var bytes = Encoding.UTF8.GetBytes("sealed body");

        var result = await writer.AppendAsync(
            new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, "k1")
            {
                Message = new MessageBody(bytes, keyId),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(gc, result.GcId);
        Assert.Equal(1L, result.Seq);
        Assert.False(result.Duplicate);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));

        var eventRows = await RowsAsync(
            "SELECT event_id, message_id, version, client_msg_id FROM gc_event WHERE gc_id = $1 AND seq = 1", gc);
        var ev = Assert.Single(eventRows);
        Assert.Equal(result.EventId, ev[0]);
        Assert.NotEqual(DBNull.Value, ev[1]);
        Assert.Equal(1, ev[2]);
        Assert.Equal("k1", ev[3]);
        var messageId = (Guid)ev[1];

        var messageRows = await RowsAsync(
            "SELECT first_seq, author_id FROM gc_message WHERE gc_id = $1 AND message_id = $2", gc, messageId);
        var msg = Assert.Single(messageRows);
        Assert.Equal(1L, msg[0]);
        Assert.Equal(author, msg[1]);

        var versionRows = await RowsAsync(
            "SELECT version, body_ct, key_id, editor_id FROM gc_message_version WHERE gc_id = $1 AND message_id = $2", gc, messageId);
        var ver = Assert.Single(versionRows);
        Assert.Equal(1, ver[0]);
        Assert.Equal(bytes, ver[1]);
        Assert.Equal(keyId, ver[2]);
        Assert.Equal(author, ver[3]);

        Assert.Equal([(gc, 1L)], notifier.Calls);
    }

    [Fact]
    public async Task Mentions_insert_one_row_per_distinct_target()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        await writer.AppendAsync(Message(gc, author, "m1", mentions: [alice, bob, alice]), TestContext.Current.CancellationToken);
        await writer.AppendAsync(Message(gc, author, "m2"), TestContext.Current.CancellationToken);

        var rows = await RowsAsync("SELECT target_principal_id FROM gc_mention WHERE gc_id = $1 AND seq = 1 ORDER BY target_principal_id", gc);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new HashSet<Guid> { alice, bob }, rows.Select(r => (Guid)r[0]).ToHashSet());
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_mention WHERE gc_id = $1 AND seq = 2", gc));
    }

    [Fact]
    public async Task Archived_gc_throws_and_does_not_notify()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync(archived: true);
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        await Assert.ThrowsAsync<GcArchivedException>(() =>
            writer.AppendAsync(Message(gc, Guid.CreateVersion7(), "k1"), TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Unknown_gc_throws_not_found_and_does_not_notify()
    {
        GcTestSkip.WithoutDatabase();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);
        var missing = Guid.CreateVersion7();

        await Assert.ThrowsAsync<GcNotFoundException>(() =>
            writer.AppendAsync(Message(missing, Guid.CreateVersion7(), "k1"), TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public async Task Repeat_of_a_key_on_an_archived_gc_returns_the_original()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        var first = await writer.AppendAsync(Message(gc, author, "k1"), TestContext.Current.CancellationToken);
        await ArchiveAsync(gc);

        var again = await writer.AppendAsync(Message(gc, author, "k1", text: "different text"), TestContext.Current.CancellationToken);

        Assert.True(again.Duplicate);
        Assert.Equal(first.Seq, again.Seq);
        Assert.Equal(first.EventId, again.EventId);
        Assert.Equal(StoredPrecision(first.CreatedAt), StoredPrecision(again.CreatedAt));
        Assert.Single(notifier.Calls);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task Archived_gc_rejects_a_new_key_even_when_a_repeat_is_in_the_batch()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);
        await writer.AppendAsync(Message(gc, author, "old"), TestContext.Current.CancellationToken);
        await ArchiveAsync(gc);

        await Assert.ThrowsAsync<GcArchivedException>(() => writer.AppendManyAsync(
            [Message(gc, author, "old"), Message(gc, author, "new")], TestContext.Current.CancellationToken));

        Assert.Single(notifier.Calls);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task Empty_client_msg_id_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.AppendAsync(Message(gc, Guid.CreateVersion7(), ""), TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Overlong_client_msg_id_is_rejected_and_the_limit_itself_is_accepted()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var limit = AppendRequest.MaxClientMsgIdLength;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.AppendAsync(Message(gc, author, new string('x', limit + 1)), TestContext.Current.CancellationToken));
        await AssertNothingWrittenAsync(gc);

        var accepted = await writer.AppendAsync(Message(gc, author, new string('x', limit)), TestContext.Current.CancellationToken);
        Assert.Equal(1L, accepted.Seq);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_kind_is_rejected(string kind)
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);
        var request = new AppendRequest(gc, kind, AuthorKind.Agent, Guid.CreateVersion7(), SourceTrust.Agent, "k1");

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(request, TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Message_kind_without_a_body_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var request = new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, Guid.CreateVersion7(), SourceTrust.MemberHuman, "k1");

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(request, TestContext.Current.CancellationToken));

        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Non_message_kind_with_a_body_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var request = new AppendRequest(gc, GcEventKinds.Structured, AuthorKind.Agent, Guid.CreateVersion7(), SourceTrust.Agent, "k1")
        {
            Message = Body("not a message"),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(request, TestContext.Current.CancellationToken));

        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Mentions_on_a_non_message_are_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var request = new AppendRequest(gc, GcEventKinds.Structured, AuthorKind.Agent, Guid.CreateVersion7(), SourceTrust.Agent, "k1")
        {
            Mentions = [Guid.CreateVersion7()],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(request, TestContext.Current.CancellationToken));

        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Mixed_gc_ids_in_one_batch_are_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var other = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendManyAsync(
            [Structured(gc, author, "a"), Structured(other, author, "b")], TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
        await AssertNothingWrittenAsync(gc);
        await AssertNothingWrittenAsync(other);
    }

    [Fact]
    public async Task Duplicate_key_inside_one_batch_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendManyAsync(
            [Structured(gc, author, "same"), Structured(gc, author, "other"), Structured(gc, author, "same")],
            TestContext.Current.CancellationToken));

        Assert.Empty(notifier.Calls);
        await AssertNothingWrittenAsync(gc);
    }

    [Fact]
    public async Task Batch_gets_consecutive_seqs_in_request_order_and_notifies_once()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        var results = await writer.AppendManyAsync(
            [Structured(gc, author, "k1"), Structured(gc, author, "k2"), Structured(gc, author, "k3")],
            TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], results.Select(r => r.Seq).ToArray());
        Assert.All(results, r => Assert.False(r.Duplicate));
        Assert.Equal(3, results.Select(r => r.EventId).Distinct().Count());
        Assert.Equal("k1,k2,k3", await db.ScalarAsync<string>(
            "SELECT string_agg(client_msg_id, ',' ORDER BY seq) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal([(gc, 3L)], notifier.Calls);
    }

    [Fact]
    public async Task Batch_with_a_used_key_returns_the_original_and_appends_the_rest_consecutively()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);
        var first = await writer.AppendAsync(Structured(gc, author, "old"), TestContext.Current.CancellationToken);

        var results = await writer.AppendManyAsync(
            [Structured(gc, author, "a"), Structured(gc, author, "old"), Structured(gc, author, "b")],
            TestContext.Current.CancellationToken);

        Assert.Equal([2L, 1L, 3L], results.Select(r => r.Seq).ToArray());
        Assert.False(results[0].Duplicate);
        Assert.True(results[1].Duplicate);
        Assert.Equal(first.EventId, results[1].EventId);
        Assert.Equal(StoredPrecision(first.CreatedAt), StoredPrecision(results[1].CreatedAt));
        Assert.False(results[2].Duplicate);
        Assert.Equal("old,a,b", await db.ScalarAsync<string>(
            "SELECT string_agg(client_msg_id, ',' ORDER BY seq) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal(2, notifier.Calls.Count);
        Assert.Equal((gc, 3L), notifier.Calls[1]);
    }

    [Fact]
    public async Task Same_client_msg_id_from_two_authors_makes_two_events()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var authorA = Guid.CreateVersion7();
        var authorB = Guid.CreateVersion7();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        var a = await writer.AppendAsync(Structured(gc, authorA, "same"), TestContext.Current.CancellationToken);
        var b = await writer.AppendAsync(Structured(gc, authorB, "same"), TestContext.Current.CancellationToken);

        Assert.False(a.Duplicate);
        Assert.False(b.Duplicate);
        Assert.Equal(1L, a.Seq);
        Assert.Equal(2L, b.Seq);
        Assert.Equal(2L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM gc_event WHERE gc_id = $1 AND client_msg_id = 'same'", gc));
    }

    [Fact]
    public async Task Optional_fields_round_trip_and_absent_ones_stay_null()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var author = Guid.CreateVersion7();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        await writer.AppendAsync(Message(gc, author, "seed"), TestContext.Current.CancellationToken);

        var session = Guid.CreateVersion7();
        const string taint = """["external:mail-7", "web:example.org"]""";
        const string payload = """{"reason": "injection-suspected", "score": 0.92}""";
        await writer.AppendAsync(
            new AppendRequest(gc, GcEventKinds.Quarantine, AuthorKind.System, author, SourceTrust.Quarantined, "q1")
            {
                SessionId = session,
                TaintedAtWrite = true,
                TaintSourcesJson = taint,
                TargetSeq = 1,
                ReplyToSeq = 1,
                PayloadJson = payload,
            },
            TestContext.Current.CancellationToken);

        var set = Assert.Single(await RowsAsync(
            """
            SELECT session_id, tainted_at_write, taint_sources = $2::jsonb, target_seq, reply_to_seq, payload = $3::jsonb,
                   author_kind, source_trust
            FROM gc_event WHERE gc_id = $1 AND seq = 2
            """, gc, taint, payload));
        Assert.Equal(session, set[0]);
        Assert.Equal(true, set[1]);
        Assert.Equal(true, set[2]);
        Assert.Equal(1L, set[3]);
        Assert.Equal(1L, set[4]);
        Assert.Equal(true, set[5]);
        Assert.Equal("system", set[6]);
        Assert.Equal("quarantined", set[7]);

        var absent = Assert.Single(await RowsAsync(
            """
            SELECT session_id IS NULL, tainted_at_write, taint_sources IS NULL, target_seq IS NULL,
                   reply_to_seq IS NULL, payload IS NULL
            FROM gc_event WHERE gc_id = $1 AND seq = 1
            """, gc));
        Assert.Equal(true, absent[0]);
        Assert.Equal(false, absent[1]);
        Assert.Equal(true, absent[2]);
        Assert.Equal(true, absent[3]);
        Assert.Equal(true, absent[4]);
        Assert.Equal(true, absent[5]);
    }

    [Fact]
    public async Task Author_kind_and_source_trust_are_stored_under_their_wire_names()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var pairs = (
            from kind in Enum.GetValues<AuthorKind>()
            from trust in Enum.GetValues<SourceTrust>()
            select (kind, trust)).ToList();
        var requests = pairs
            .Select((p, i) => new AppendRequest(gc, GcEventKinds.Structured, p.kind, Guid.CreateVersion7(), p.trust, $"enum-{i}"))
            .ToList();

        await writer.AppendManyAsync(requests, TestContext.Current.CancellationToken);

        var expected = pairs.Select(p => (Wire(p.kind), Wire(p.trust))).ToList();
        var stored = (await RowsAsync("SELECT author_kind, source_trust FROM gc_event WHERE gc_id = $1 ORDER BY seq", gc))
            .Select(r => ((string)r[0], (string)r[1]))
            .ToList();
        Assert.Equal(expected, stored);
    }
}
