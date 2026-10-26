using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>G2: the seq counter under concurrency, rollback, idempotency, and notify-after-commit (docs/design/01 Testing).</summary>
public sealed class GcLogWriterConcurrencyTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fifty_concurrent_writers_to_one_GC_get_seq_1_to_50_with_no_gap()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => writer.AppendAsync(GcTestData.System(gc, $"k{i}"), Ct), Ct)));

        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), results.Select(r => r.Seq).Order());
        Assert.All(results, r => Assert.False(r.Duplicate));
        Assert.Equal(50L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal(50L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(50, notifier.Calls.Count);
    }

    [Fact]
    public async Task Concurrent_writers_across_GCs_keep_each_GC_gap_free()
    {
        GcTestSkip.WithoutDatabase();
        var gcs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.CreateGcAsync()));
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        await Task.WhenAll(gcs.SelectMany(gc => Enumerable.Range(0, 25).Select(i =>
            Task.Run(() => writer.AppendAsync(GcTestData.System(gc, $"k{i}"), Ct), Ct))));

        foreach (var gc in gcs)
        {
            Assert.Equal(25L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
            Assert.Equal(25L, await db.ScalarAsync<long>("SELECT count(DISTINCT seq) FROM gc_event WHERE gc_id = $1 AND seq BETWEEN 1 AND 25", gc));
        }
    }

    [Fact]
    public async Task A_failed_append_rolls_back_the_counter_and_leaves_no_gap_and_no_notification()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        var first = await writer.AppendAsync(GcTestData.System(gc, "a"), Ct);
        // An unregistered kind violates the gc_event_kind foreign key after head_seq was already advanced in the transaction.
        var bad = GcTestData.System(gc, "b") with { Kind = "not_a_registered_kind" };
        await Assert.ThrowsAsync<PostgresException>(() => writer.AppendAsync(bad, Ct));
        var next = await writer.AppendAsync(GcTestData.System(gc, "c"), Ct);

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, next.Seq);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal([(gc, 1L), (gc, 2L)], notifier.Calls);
    }

    [Fact]
    public async Task A_failed_batch_leaves_no_partial_events()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        var batch = new[]
        {
            GcTestData.System(gc, "ok-1"),
            GcTestData.System(gc, "ok-2"),
            GcTestData.System(gc, "bad") with { Kind = "not_a_registered_kind" },
        };
        await Assert.ThrowsAsync<PostgresException>(() => writer.AppendManyAsync(batch, Ct));

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(1, (await writer.AppendAsync(GcTestData.System(gc, "ok-1"), Ct)).Seq);
    }

    [Fact]
    public async Task A_duplicate_key_returns_the_same_event_and_consumes_no_seq()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var notifier = new RecordingNotifier();
        var writer = new GcLogWriter(db.DataSource, notifier);

        var original = await writer.AppendAsync(GcTestData.System(gc, "same"), Ct);
        var repeat = await writer.AppendAsync(GcTestData.System(gc, "same") with { PayloadJson = """{"changed":true}""" }, Ct);
        var other = await writer.AppendAsync(GcTestData.System(gc, "different"), Ct);

        Assert.False(original.Duplicate);
        Assert.True(repeat.Duplicate);
        Assert.Equal(original.Seq, repeat.Seq);
        Assert.Equal(original.EventId, repeat.EventId);
        Assert.Equal(original.CreatedAt, repeat.CreatedAt);
        Assert.Equal(2, other.Seq);
        Assert.Equal(2, notifier.Calls.Count); // the repeat wrote nothing and notified nothing
    }

    [Fact]
    public async Task Twenty_racing_retries_of_one_key_write_exactly_one_event()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => writer.AppendAsync(GcTestData.System(gc, "retry"), Ct), Ct)));

        Assert.Single(results.Select(r => r.Seq).Distinct());
        Assert.Equal(1, results.Count(r => !r.Duplicate));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task Rows_are_append_only()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        await new GcLogWriter(db.DataSource, new RecordingNotifier()).AppendAsync(GcTestData.System(gc, "a"), Ct);

        await using var update = db.DataSource.CreateCommand("UPDATE gc_event SET kind = 'structured'");
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));
        await using var delete = db.DataSource.CreateCommand("DELETE FROM gc_event");
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));
    }
}
