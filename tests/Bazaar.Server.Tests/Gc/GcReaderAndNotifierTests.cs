using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>Tests for <see cref="GcLogReader"/> (viewer rules, paging, field mapping) and <see cref="CoalescingGcNotifier"/> (NOTIFY hints).</summary>
public sealed class GcReaderAndNotifierTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _systemId = Guid.CreateVersion7();
    private static readonly GcPrincipal _viewer = new(Guid.CreateVersion7(), PrincipalKind.Owner, "Owner");

    private sealed class NoopNotifier : IGcNotifier
    {
        public static readonly NoopNotifier Instance = new();

        public void Notify(Guid gcId, long seq)
        {
        }
    }

    /// <summary>A fixed grant for every read.</summary>
    private sealed class FixedAccess(GcReadGrant grant) : IGcReadAccess
    {
        public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) => ValueTask.FromResult(grant);
    }

    private static async Task SeedSystemEventsAsync(GcLogWriter writer, Guid gc, int count, CancellationToken ct)
    {
        const int batchSize = 100;
        for (var start = 0; start < count; start += batchSize)
        {
            var requests = Enumerable.Range(start, Math.Min(batchSize, count - start))
                .Select(i => new AppendRequest(gc, GcEventKinds.System, AuthorKind.System, _systemId, SourceTrust.System, $"e{i}"))
                .ToList();
            await writer.AppendManyAsync(requests, ct);
        }
    }

    private static async Task<List<GcEvent>> ReadAllAsync(GcLogReader reader, Guid gc, long afterSeq, int limit, GcPrincipal viewer, CancellationToken ct)
    {
        var events = new List<GcEvent>();
        await foreach (var e in reader.ReadAsync(gc, afterSeq, limit, viewer, ct))
            events.Add(e);
        return events;
    }

    private static void AssertJsonEquivalent(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected JSON {expected} but read {actual}.");
    }

    // ---------------------------------------------------------------- Reader

    [Fact]
    public async Task Read_after_seq_returns_following_events_in_order_and_respects_limit()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await SeedSystemEventsAsync(new GcLogWriter(db.DataSource, NoopNotifier.Instance), gc, 10, ct);
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full));

        var page = await ReadAllAsync(reader, gc, afterSeq: 4, limit: 3, _viewer, ct);

        Assert.Equal([5L, 6L, 7L], page.Select(e => e.Seq));
    }

    [Fact]
    public async Task Paging_by_last_seq_reproduces_the_whole_log_without_duplicates()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await SeedSystemEventsAsync(new GcLogWriter(db.DataSource, NoopNotifier.Instance), gc, 250, ct);
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full));

        var all = new List<GcEvent>();
        var after = 0L;
        var pageSizes = new List<int>();
        while (true)
        {
            var page = await ReadAllAsync(reader, gc, after, limit: 100, _viewer, ct);
            pageSizes.Add(page.Count);
            if (page.Count == 0)
                break;
            all.AddRange(page);
            after = page[^1].Seq;
        }

        Assert.Equal([100, 100, 50, 0], pageSizes);
        Assert.Equal(250, all.Count);
        Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), all.Select(e => e.Seq));
        Assert.Equal(all.Count, all.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task Fields_round_trip_into_GcEvent()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, NoopNotifier.Instance);
        var human = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        var bridge = Guid.CreateVersion7();

        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, human, SourceTrust.MemberHuman, "m1")
        {
            Message = new MessageBody(new byte[] { 1, 2, 3 }, Guid.CreateVersion7()),
            Mentions = [Guid.CreateVersion7()],
        }, ct);
        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.Structured, AuthorKind.Agent, agent, SourceTrust.External, "s1")
        {
            TaintedAtWrite = true,
            TaintSourcesJson = """["src-a","src-b"]""",
            TargetSeq = 1,
            ReplyToSeq = 1,
            PayloadJson = """{"title":"x","n":2}""",
        }, ct);
        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.ExternalMail, AuthorKind.ExternalBridge, bridge, SourceTrust.Quarantined, "x1"), ct);
        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.System, AuthorKind.Owner, _viewer.Id, SourceTrust.Owner, "o1"), ct);

        var events = await ReadAllAsync(new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full)), gc, 0, 100, _viewer, ct);

        Assert.Equal(4, events.Count);

        var message = events[0];
        Assert.Equal(gc, message.GcId);
        Assert.Equal(1L, message.Seq);
        Assert.NotEqual(Guid.Empty, message.EventId);
        Assert.Equal(GcEventKinds.Message, message.Kind);
        Assert.Equal(AuthorKind.Human, message.AuthorKind);
        Assert.Equal(human, message.AuthorId);
        Assert.Equal(SourceTrust.MemberHuman, message.SourceTrust);
        Assert.False(message.TaintedAtWrite);
        Assert.Null(message.TaintSourcesJson);
        Assert.NotNull(message.MessageId);
        Assert.Equal(1, message.Version);
        Assert.Null(message.TargetSeq);
        Assert.Null(message.ReplyToSeq);
        Assert.Null(message.PayloadJson);

        var structured = events[1];
        Assert.Equal(2L, structured.Seq);
        Assert.Equal(GcEventKinds.Structured, structured.Kind);
        Assert.Equal(AuthorKind.Agent, structured.AuthorKind);
        Assert.Equal(agent, structured.AuthorId);
        Assert.Equal(SourceTrust.External, structured.SourceTrust);
        Assert.True(structured.TaintedAtWrite);
        AssertJsonEquivalent("""["src-a","src-b"]""", structured.TaintSourcesJson);
        Assert.Null(structured.MessageId);
        Assert.Null(structured.Version);
        Assert.Equal(1L, structured.TargetSeq);
        Assert.Equal(1L, structured.ReplyToSeq);
        AssertJsonEquivalent("""{"title":"x","n":2}""", structured.PayloadJson);

        var mail = events[2];
        Assert.Equal(3L, mail.Seq);
        Assert.Equal(GcEventKinds.ExternalMail, mail.Kind);
        Assert.Equal(AuthorKind.ExternalBridge, mail.AuthorKind);
        Assert.Equal(bridge, mail.AuthorId);
        Assert.Equal(SourceTrust.Quarantined, mail.SourceTrust);
        Assert.False(mail.TaintedAtWrite);
        Assert.Null(mail.TaintSourcesJson);
        Assert.Null(mail.PayloadJson);

        var owner = events[3];
        Assert.Equal(AuthorKind.Owner, owner.AuthorKind);
        Assert.Equal(SourceTrust.Owner, owner.SourceTrust);
    }

    [Fact]
    public async Task Denied_grant_throws_GcAccessDeniedException_on_enumeration()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await SeedSystemEventsAsync(new GcLogWriter(db.DataSource, NoopNotifier.Instance), gc, 3, ct);
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Denied));

        var ex = await Assert.ThrowsAsync<GcAccessDeniedException>(() => ReadAllAsync(reader, gc, 0, 10, _viewer, ct));

        Assert.Equal(gc, ex.GcId);
    }

    [Fact]
    public async Task Through_grant_never_returns_events_past_the_grant()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await SeedSystemEventsAsync(new GcLogWriter(db.DataSource, NoopNotifier.Instance), gc, 10, ct);
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Through(5)));

        var visible = await ReadAllAsync(reader, gc, 0, IGcLogReader.MaxLimit, _viewer, ct);
        var after = await ReadAllAsync(reader, gc, 5, IGcLogReader.MaxLimit, _viewer, ct);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], visible.Select(e => e.Seq));
        Assert.Empty(after);
    }

    [Fact]
    public async Task Invalid_arguments_throw_ArgumentOutOfRange()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAllAsync(reader, gc, 0, 0, _viewer, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAllAsync(reader, gc, 0, IGcLogReader.MaxLimit + 1, _viewer, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAllAsync(reader, gc, -1, 10, _viewer, ct));
    }

    [Fact]
    public async Task GetHeadSeqAsync_returns_head_and_throws_for_unknown_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await SeedSystemEventsAsync(new GcLogWriter(db.DataSource, NoopNotifier.Instance), gc, 7, ct);
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full));

        Assert.Equal(7L, await reader.GetHeadSeqAsync(gc, ct));

        var unknown = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<GcNotFoundException>(() => reader.GetHeadSeqAsync(unknown, ct));
        Assert.Equal(unknown, ex.GcId);
    }

    [Fact]
    public async Task Reading_an_empty_gc_yields_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = new GcLogReader(db.DataSource, new FixedAccess(GcReadGrant.Full));

        var events = await ReadAllAsync(reader, gc, 0, 100, _viewer, ct);

        Assert.Empty(events);
        Assert.Equal(0L, await reader.GetHeadSeqAsync(gc, ct));
    }

    // ---------------------------------------------------------------- Notifier

    /// <summary>Opens a pooled connection on <c>LISTEN gc</c> and collects the raw payloads it receives.</summary>
    private static async Task<NpgsqlConnection> ListenAsync(GcDb db, ConcurrentQueue<string> sink, CancellationToken ct)
    {
        var listener = await db.OpenConnectionAsync(ct);
        listener.Notification += (_, e) =>
        {
            if (e.Channel == _gcNotifyChannelName)
                sink.Enqueue(e.Payload);
        };
        await using var listen = new NpgsqlCommand($"LISTEN {_gcNotifyChannelName}", listener);
        await listen.ExecuteNonQueryAsync(ct);
        return listener;
    }

    private const string _gcNotifyChannelName = "gc";

    /// <summary>Runs a round trip on the listener so every notification committed so far has been delivered to it.</summary>
    private static async Task<List<(Guid Gc, long Seq)>> DrainAsync(NpgsqlConnection listener, ConcurrentQueue<string> sink, CancellationToken ct)
    {
        await using var sync = new NpgsqlCommand("SELECT 1", listener);
        await sync.ExecuteNonQueryAsync(ct);
        return [.. sink.Select(ParsePayload)];
    }

    private static (Guid Gc, long Seq) ParsePayload(string payload)
    {
        var node = JsonNode.Parse(payload)!;
        return (Guid.Parse(node["gc"]!.GetValue<string>()), node["seq"]!.GetValue<long>());
    }

    [Fact]
    public async Task FlushAsync_sends_one_notification_with_the_newest_seq_for_a_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var sink = new ConcurrentQueue<string>();
        await using var listener = await ListenAsync(db, sink, ct);
        // The window is long so only the explicit FlushAsync below can send; the loop is started but never flushes on its own.
        await using var notifier = new CoalescingGcNotifier(db.DataSource, new GcNotifierOptions(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2)));
        await notifier.StartAsync(ct);

        // A permutation of 1..100, so the seqs arrive out of order and the newest (100) is not last.
        foreach (var i in Enumerable.Range(1, 100))
            notifier.Notify(gc, (i * 37) % 100 + 1);

        await notifier.FlushAsync(ct);
        var received = await DrainAsync(listener, sink, ct);

        var hint = Assert.Single(received, r => r.Gc == gc);
        Assert.Equal(100L, hint.Seq);
        Assert.Single(received);
    }

    [Fact]
    public async Task Hints_for_three_gcs_go_out_in_one_flush()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gcA = await db.CreateGcAsync();
        var gcB = await db.CreateGcAsync();
        var gcC = await db.CreateGcAsync();
        var sink = new ConcurrentQueue<string>();
        await using var listener = await ListenAsync(db, sink, ct);
        await using var notifier = new CoalescingGcNotifier(db.DataSource, new GcNotifierOptions(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(2)));
        await notifier.StartAsync(ct);

        notifier.Notify(gcA, 3);
        notifier.Notify(gcB, 7);
        notifier.Notify(gcC, 1);
        var before = notifier.FlushCount;

        await notifier.FlushAsync(ct);

        Assert.Equal(before + 1, notifier.FlushCount);
        var received = await DrainAsync(listener, sink, ct);
        Assert.Equal(3, received.Count);
        Assert.Equal(new Dictionary<Guid, long> { [gcA] = 3, [gcB] = 7, [gcC] = 1 }, received.ToDictionary(r => r.Gc, r => r.Seq));
    }

    [Fact]
    public async Task Background_loop_delivers_a_hint_without_an_explicit_flush()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var sink = new ConcurrentQueue<string>();
        await using var listener = await ListenAsync(db, sink, ct);
        await using var notifier = new CoalescingGcNotifier(db.DataSource, new GcNotifierOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2)));
        await notifier.StartAsync(ct);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var started = Stopwatch.GetTimestamp();
        notifier.Notify(gc, 42);

        try
        {
            // WaitAsync returns on each message the listener receives, so loop until the hint arrives or the deadline passes.
            while (sink.IsEmpty)
                await listener.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail("No GC hint arrived within 2 s of Notify, with no FlushAsync call.");
        }

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        Assert.Equal([(gc, 42L)], (await DrainAsync(listener, sink, ct)));
        Assert.True(notifier.FlushCount >= 1);
    }

    [Fact]
    public async Task FlushAsync_against_an_unreachable_database_throws_and_keeps_the_hint_pending()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = Guid.CreateVersion7();
        await using var deadSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=postgres;Database=postgres;Timeout=2;Pooling=false");
        await using var notifier = new CoalescingGcNotifier(deadSource, new GcNotifierOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(2)));

        notifier.Notify(gc, 9);

        await Assert.ThrowsAnyAsync<NpgsqlException>(() => notifier.FlushAsync(ct));
        // The hint was requeued, so a second flush still tries to send it and throws again instead of returning silently.
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => notifier.FlushAsync(ct));
        Assert.Equal(0, notifier.FlushCount);
    }
}
