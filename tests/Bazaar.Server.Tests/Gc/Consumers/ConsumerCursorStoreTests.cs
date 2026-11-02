using Bazaar.Server.Gc.Consumers;

namespace Bazaar.Server.Tests.Gc.Consumers;

/// <summary>Tests for <see cref="ConsumerCursorStore"/> over <c>consumer_cursor</c>.</summary>
public sealed class ConsumerCursorStoreTests(GcDb db) : IClassFixture<GcDb>
{
    [Fact]
    public async Task Unknown_consumer_starts_at_zero()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new ConsumerCursorStore(db.DataSource);

        Assert.Equal(0, await store.GetAsync("never-advanced", gc, ct));
    }

    [Fact]
    public async Task Advance_moves_forward_and_a_lower_seq_is_a_no_op_that_reports_the_stored_value()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new ConsumerCursorStore(db.DataSource);

        Assert.Equal(5, await store.AdvanceAsync("unread", gc, 5, ct));
        Assert.Equal(5, await store.AdvanceAsync("unread", gc, 3, ct));
        Assert.Equal(5, await store.AdvanceAsync("unread", gc, 5, ct));
        Assert.Equal(5, await store.GetAsync("unread", gc, ct));

        Assert.Equal(9, await store.AdvanceAsync("unread", gc, 9, ct));
        Assert.Equal(9, await store.GetAsync("unread", gc, ct));
    }

    [Fact]
    public async Task Advancing_a_new_cursor_to_zero_creates_it_at_zero()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new ConsumerCursorStore(db.DataSource);

        // Zero is the starting point, so advancing to zero creates the row and leaves it at zero.
        Assert.Equal(0, await store.AdvanceAsync("fresh", gc, 0, ct));
        Assert.Equal(0, await store.GetAsync("fresh", gc, ct));
    }

    [Fact]
    public async Task Cursors_are_keyed_by_consumer_and_by_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gcA = await db.CreateGcAsync();
        var gcB = await db.CreateGcAsync();
        var store = new ConsumerCursorStore(db.DataSource);

        await store.AdvanceAsync("notifier", gcA, 4, ct);
        await store.AdvanceAsync("unread", gcA, 7, ct);
        await store.AdvanceAsync("notifier", gcB, 2, ct);

        Assert.Equal(4, await store.GetAsync("notifier", gcA, ct));
        Assert.Equal(7, await store.GetAsync("unread", gcA, ct));
        Assert.Equal(2, await store.GetAsync("notifier", gcB, ct));
        Assert.Equal(0, await store.GetAsync("unread", gcB, ct));
    }

    [Fact]
    public async Task Negative_seq_is_rejected()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var store = new ConsumerCursorStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AdvanceAsync("unread", gc, -1, ct));
    }
}
