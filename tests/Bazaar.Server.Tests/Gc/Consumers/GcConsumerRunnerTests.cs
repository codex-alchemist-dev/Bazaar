using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Consumers;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Tests.Gc.Consumers;

/// <summary>Tests for <see cref="GcConsumerRunner"/>: cursor reads, batching, and at-least-once delivery.</summary>
public sealed class GcConsumerRunnerTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _author = Guid.CreateVersion7();
    private static readonly Guid _viewerId = Guid.CreateVersion7();

    private GcConsumerRunner CreateRunner() =>
        new(new GcLogReader(db.DataSource, new AllowAllAccess()), new ConsumerCursorStore(db.DataSource));

    [Fact]
    public async Task Runner_delivers_events_after_the_cursor_in_seq_order_and_advances_to_head()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _author, 5, "a", ct);
        var consumer = new RecordingConsumer("runner-head");
        var cursors = new ConsumerCursorStore(db.DataSource);

        var delivered = await CreateRunner().RunToHeadAsync(consumer, gc, ConsumerTestSupport.Principal(_viewerId), ct: ct);

        Assert.Equal(5, delivered);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], consumer.Seqs);
        Assert.Equal(5, await cursors.GetAsync("runner-head", gc, ct));

        // A second run with nothing new delivers nothing and does not call the consumer again.
        var again = await CreateRunner().RunToHeadAsync(consumer, gc, ConsumerTestSupport.Principal(_viewerId), ct: ct);
        Assert.Equal(0, again);
        Assert.Equal(1, consumer.Calls);
    }

    [Fact]
    public async Task Runner_hands_over_batches_of_the_requested_size()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _author, 5, "batch", ct);
        var consumer = new RecordingConsumer("runner-batches");

        await CreateRunner().RunToHeadAsync(consumer, gc, ConsumerTestSupport.Principal(_viewerId), batchSize: 2, ct: ct);

        Assert.Equal(3, consumer.Calls);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], consumer.Seqs);
    }

    [Fact]
    public async Task Runner_does_not_advance_the_cursor_when_the_consumer_fails()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _author, 3, "fail", ct);
        var cursors = new ConsumerCursorStore(db.DataSource);
        var failing = new RecordingConsumer("runner-fail", failOnCall: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner().RunToHeadAsync(failing, gc, ConsumerTestSupport.Principal(_viewerId), ct: ct));

        Assert.Equal(0, await cursors.GetAsync("runner-fail", gc, ct));
        Assert.Empty(failing.Seqs);

        // The same consumer name, healthy this time, gets the whole batch again.
        var healthy = new RecordingConsumer("runner-fail");
        await CreateRunner().RunToHeadAsync(healthy, gc, ConsumerTestSupport.Principal(_viewerId), ct: ct);
        Assert.Equal([1L, 2L, 3L], healthy.Seqs);
    }

    [Fact]
    public async Task A_failed_batch_leaves_the_cursor_at_the_last_committed_batch()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _author, 5, "partial", ct);
        var cursors = new ConsumerCursorStore(db.DataSource);
        var failsSecond = new RecordingConsumer("runner-partial", failOnCall: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner().RunToHeadAsync(failsSecond, gc, ConsumerTestSupport.Principal(_viewerId), batchSize: 2, ct: ct));

        Assert.Equal(2, await cursors.GetAsync("runner-partial", gc, ct));
        Assert.Equal([1L, 2L], failsSecond.Seqs);

        var healthy = new RecordingConsumer("runner-partial");
        await CreateRunner().RunToHeadAsync(healthy, gc, ConsumerTestSupport.Principal(_viewerId), batchSize: 2, ct: ct);
        Assert.Equal([3L, 4L, 5L], healthy.Seqs);
    }

    [Fact]
    public async Task Runner_applies_the_viewer_rules_and_does_not_advance_when_the_read_is_denied()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _author, 2, "denied", ct);
        var cursors = new ConsumerCursorStore(db.DataSource);
        var runner = new GcConsumerRunner(new GcLogReader(db.DataSource, new DenyAllAccess()), cursors);

        await Assert.ThrowsAsync<GcAccessDeniedException>(() =>
            runner.RunToHeadAsync(new RecordingConsumer("runner-denied"), gc, ConsumerTestSupport.Principal(_viewerId), ct: ct));

        Assert.Equal(0, await cursors.GetAsync("runner-denied", gc, ct));
    }
}
