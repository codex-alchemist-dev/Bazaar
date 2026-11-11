using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Tests.Gc.Read;

/// <summary>Tests for <see cref="ReadStateWriter"/> over <c>read_state</c>.</summary>
public sealed class ReadStateWriterTests(GcDb db) : IClassFixture<GcDb>
{
    [Fact]
    public async Task First_write_creates_the_row_at_the_given_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        Assert.Equal(7, await writer.MarkReadAsync(reader, gc, 7, ct));
        Assert.Equal(7L, await db.ScalarAsync<long>(
            "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2", reader, gc));
    }

    [Fact]
    public async Task Forward_write_moves_the_position_ahead()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        await writer.MarkReadAsync(reader, gc, 3, ct);
        Assert.Equal(12, await writer.MarkReadAsync(reader, gc, 12, ct));
        Assert.Equal(12L, await db.ScalarAsync<long>(
            "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2", reader, gc));
    }

    [Fact]
    public async Task Backward_write_is_ignored_and_reports_the_stored_position()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        await writer.MarkReadAsync(reader, gc, 10, ct);
        Assert.Equal(10, await writer.MarkReadAsync(reader, gc, 4, ct));
        Assert.Equal(10, await writer.MarkReadAsync(reader, gc, 10, ct));
        Assert.Equal(10L, await db.ScalarAsync<long>(
            "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2", reader, gc));
    }

    [Fact]
    public async Task Zero_on_a_new_row_creates_it_at_zero()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        Assert.Equal(0, await writer.MarkReadAsync(reader, gc, 0, ct));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2", reader, gc));
    }

    [Fact]
    public async Task Concurrent_writes_keep_the_maximum()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        // Shuffled so the largest value does not land last; every writer races on the same first insert.
        var seqs = Enumerable.Range(1, 64).OrderBy(i => (i * 37) % 64).ToArray();
        await Task.WhenAll(seqs.Select(seq => writer.MarkReadAsync(reader, gc, seq, ct)));

        Assert.Equal(64L, await db.ScalarAsync<long>(
            "SELECT last_read_seq FROM read_state WHERE principal_id = $1 AND gc_id = $2", reader, gc));
    }

    [Fact]
    public async Task Positions_are_kept_per_principal_and_per_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gcA = await db.CreateGcAsync();
        var gcB = await db.CreateGcAsync();
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        await writer.MarkReadAsync(alice, gcA, 5, ct);
        await writer.MarkReadAsync(alice, gcB, 2, ct);
        await writer.MarkReadAsync(bob, gcA, 9, ct);

        Assert.Equal(5, await writer.MarkReadAsync(alice, gcA, 1, ct));
        Assert.Equal(2, await writer.MarkReadAsync(alice, gcB, 1, ct));
        Assert.Equal(9, await writer.MarkReadAsync(bob, gcA, 1, ct));
    }

    [Fact]
    public async Task Unknown_gc_is_rejected_and_writes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var unknown = Guid.CreateVersion7();
        var reader = Guid.CreateVersion7();
        var writer = new ReadStateWriter(db.DataSource);

        var ex = await Assert.ThrowsAsync<GcNotFoundException>(() => writer.MarkReadAsync(reader, unknown, 4, ct));
        Assert.Equal(unknown, ex.GcId);
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT COUNT(*) FROM read_state WHERE principal_id = $1", reader));
    }

    [Fact]
    public async Task Negative_seq_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var writer = new ReadStateWriter(db.DataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.MarkReadAsync(Guid.CreateVersion7(), gc, -1, ct));
    }
}
