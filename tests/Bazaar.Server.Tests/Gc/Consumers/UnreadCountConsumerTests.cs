using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Consumers;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Tests.Gc.Consumers;

/// <summary>Tests for <see cref="UnreadCountConsumer"/>: read cursor, own events, and viewer rules.</summary>
public sealed class UnreadCountConsumerTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _me = Guid.CreateVersion7();
    private static readonly Guid _other = Guid.CreateVersion7();

    private UnreadCountConsumer CreateConsumer() =>
        new(db.DataSource, new GcLogReader(db.DataSource, new AllowAllAccess()));

    /// <summary>Seqs 1-2 by <c>other</c>, 3-4 by <c>me</c>, 5-7 by <c>other</c>.</summary>
    private async Task<Guid> SeedMixedGcAsync(CancellationToken ct)
    {
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 2, "o1", ct);
        await ConsumerTestSupport.AppendAsync(db, gc, _me, 2, "m1", ct);
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 3, "o2", ct);
        return gc;
    }

    [Fact]
    public async Task Principal_with_no_read_state_counts_every_event_written_by_others()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await SeedMixedGcAsync(ct);

        var unread = await CreateConsumer().CountUnreadAsync(ConsumerTestSupport.Principal(_me), gc, ct);

        Assert.Equal(5, unread);
    }

    [Fact]
    public async Task Count_starts_after_the_principal_read_cursor()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await SeedMixedGcAsync(ct);
        await ConsumerTestSupport.SetReadStateAsync(db, _me, gc, 2);
        await ConsumerTestSupport.SetReadStateAsync(db, _other, gc, 7);
        var consumer = CreateConsumer();

        // Events 3 and 4 are mine, so only 5 to 7 count. The other principal has read to the head.
        Assert.Equal(3, await consumer.CountUnreadAsync(ConsumerTestSupport.Principal(_me), gc, ct));
        Assert.Equal(0, await consumer.CountUnreadAsync(ConsumerTestSupport.Principal(_other), gc, ct));
    }

    [Fact]
    public async Task Own_events_are_never_counted_as_unread()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _me, 4, "only-mine", ct);

        var unread = await CreateConsumer().CountUnreadAsync(ConsumerTestSupport.Principal(_me), gc, ct);

        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task Count_is_per_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var busy = await SeedMixedGcAsync(ct);
        var quiet = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, quiet, _other, 1, "quiet", ct);
        var consumer = CreateConsumer();

        Assert.Equal(5, await consumer.CountUnreadAsync(ConsumerTestSupport.Principal(_me), busy, ct));
        Assert.Equal(1, await consumer.CountUnreadAsync(ConsumerTestSupport.Principal(_me), quiet, ct));
    }

    [Fact]
    public async Task A_gc_the_principal_may_not_read_is_not_counted()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await SeedMixedGcAsync(ct);
        var consumer = new UnreadCountConsumer(db.DataSource, new GcLogReader(db.DataSource, new DenyAllAccess()));

        await Assert.ThrowsAsync<GcAccessDeniedException>(() =>
            consumer.CountUnreadAsync(ConsumerTestSupport.Principal(_me), gc, ct));
    }
}
