using Bazaar.Server.Memory.Write;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Memory.Write;

public class MemoryWriteQueueTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private const string _agent = "agent-7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MemoryWriteItem Item(string path, string content = "body", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), path, content, "gc-1", "task-3", "passed");

    [Fact]
    public async Task Twenty_stores_in_one_turn_give_one_commit()
    {
        var sink = new RecordingBatchSink();
        var time = new FakeTimeProvider(_start);
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), time);

        for (var i = 0; i < 20; i++)
        {
            await queue.EnqueueAsync(Item($"agents/{_agent}/note-{i}.md"), Ct);
        }

        await queue.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
        Assert.Equal(20, sink.Commits[0].Items.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Turn_flush_with_nothing_pending_makes_no_commit()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start));

        await queue.EnqueueAsync(Item("a.md"), Ct);
        await queue.FlushTurnAsync(Ct);
        await queue.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
    }

    [Fact]
    public async Task Idle_flush_happens_after_thirty_seconds_not_before()
    {
        var sink = new RecordingBatchSink();
        var time = new FakeTimeProvider(_start);
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), time);

        await queue.EnqueueAsync(Item("a.md"), Ct);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await queue.RunIdleCheckAsync(Ct));
        Assert.Empty(sink.Commits);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await queue.RunIdleCheckAsync(Ct));
        Assert.Single(sink.Commits);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Idle_window_restarts_on_each_enqueue()
    {
        var sink = new RecordingBatchSink();
        var time = new FakeTimeProvider(_start);
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), time);

        await queue.EnqueueAsync(Item("a.md"), Ct);
        time.Advance(TimeSpan.FromSeconds(20));
        await queue.EnqueueAsync(Item("b.md"), Ct);
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.False(await queue.RunIdleCheckAsync(Ct));
        Assert.Empty(sink.Commits);
    }

    [Fact]
    public async Task Max_writes_flush_at_twenty_queued()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start));

        for (var i = 0; i < 19; i++)
        {
            await queue.EnqueueAsync(Item($"n-{i}.md"), Ct);
        }

        Assert.Empty(sink.Commits);
        Assert.Equal(19, queue.PendingCount);

        await queue.EnqueueAsync(Item("n-19.md"), Ct);

        Assert.Single(sink.Commits);
        Assert.Equal(20, sink.Commits[0].Items.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Constructor_settings_override_defaults()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start), maxWrites: 2);

        await queue.EnqueueAsync(Item("a.md"), Ct);
        await queue.EnqueueAsync(Item("b.md"), Ct);

        Assert.Single(sink.Commits);
    }

    [Fact]
    public async Task Read_your_writes_sees_pending_content_before_flush()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start));

        Assert.False(queue.TryGetPending("notes/a.md", out _));

        await queue.EnqueueAsync(Item("notes/a.md", "first"), Ct);
        await queue.EnqueueAsync(Item("notes/a.md", "second"), Ct);
        await queue.EnqueueAsync(Item("notes/b.md", "other"), Ct);

        Assert.True(queue.TryGetPending("notes/a.md", out var content));
        Assert.Equal("second", content);
        Assert.True(queue.TryGetPending("notes/b.md", out var other));
        Assert.Equal("other", other);
        Assert.Empty(sink.Commits);

        await queue.FlushTurnAsync(Ct);

        Assert.False(queue.TryGetPending("notes/a.md", out _));
    }

    [Fact]
    public async Task Crash_between_spool_and_flush_replays_without_duplicates()
    {
        var spool = new InMemoryMemorySpool();
        var sink = new RecordingBatchSink();
        var time = new FakeTimeProvider(_start);
        var items = Enumerable.Range(0, 3).Select(i => Item($"c-{i}.md")).ToList();

        var before = new MemoryWriteQueue(_agent, sink, spool, time);
        foreach (var item in items)
        {
            await before.EnqueueAsync(item, Ct);
        }

        // Simulated crash: the first instance is dropped without flushing.
        Assert.Equal(3, spool.Count);
        Assert.Empty(sink.Commits);

        var after = new MemoryWriteQueue(_agent, sink, spool, time);
        await after.ReplayAsync(new HashSet<Guid>(), Ct);
        await after.ReplayAsync(new HashSet<Guid>(), Ct);
        Assert.Equal(3, after.PendingCount);
        Assert.Equal(3, spool.Count);

        await after.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
        Assert.Equal(items.Select(i => i.WriteId), sink.Commits[0].Items.Select(i => i.WriteId));
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public async Task Replay_skips_writes_already_in_the_committed_set()
    {
        var spool = new InMemoryMemorySpool();
        var sink = new RecordingBatchSink();
        var time = new FakeTimeProvider(_start);
        var committedItem = Item("done.md");
        var openItem = Item("open.md");

        var before = new MemoryWriteQueue(_agent, sink, spool, time);
        await before.EnqueueAsync(committedItem, Ct);
        await before.EnqueueAsync(openItem, Ct);

        // The commit landed but the spool removal was lost: the spool still holds the committed write.
        var after = new MemoryWriteQueue(_agent, sink, spool, time);
        await after.ReplayAsync(new HashSet<Guid> { committedItem.WriteId }, Ct);

        Assert.Equal(1, after.PendingCount);
        Assert.Equal(1, spool.Count);

        await after.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
        Assert.Equal(openItem.WriteId, sink.Commits[0].Items.Single().WriteId);
    }

    [Fact]
    public async Task Duplicate_enqueue_of_same_write_id_is_ignored()
    {
        var spool = new InMemoryMemorySpool();
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, spool, new FakeTimeProvider(_start));
        var id = Guid.NewGuid();

        await queue.EnqueueAsync(Item("x.md", "one", id), Ct);
        await queue.EnqueueAsync(Item("x.md", "one", id), Ct);
        await queue.FlushTurnAsync(Ct);
        await queue.EnqueueAsync(Item("x.md", "one", id), Ct);
        await queue.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
        Assert.Single(sink.Commits[0].Items);
    }

    [Fact]
    public async Task Failed_commit_keeps_items_in_queue_and_spool_then_retry_commits_them()
    {
        var spool = new InMemoryMemorySpool();
        var sink = new RecordingBatchSink { FailNext = 1 };
        var queue = new MemoryWriteQueue(_agent, sink, spool, new FakeTimeProvider(_start));

        await queue.EnqueueAsync(Item("f-1.md"), Ct);
        await queue.EnqueueAsync(Item("f-2.md"), Ct);

        await queue.FlushTurnAsync(Ct);

        Assert.Empty(sink.Commits);
        Assert.Equal(2, queue.PendingCount);
        Assert.Equal(2, spool.Count);
        Assert.True(queue.TryGetPending("f-2.md", out _));

        await queue.FlushTurnAsync(Ct);

        Assert.Single(sink.Commits);
        Assert.Equal(2, sink.Commits[0].Items.Count);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, spool.Count);
    }

    [Fact]
    public async Task Batch_trailers_summarise_items()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start));

        await queue.EnqueueAsync(new MemoryWriteItem(Guid.NewGuid(), "a.md", "x", "gc-b", "task-2", "passed"), Ct);
        await queue.EnqueueAsync(new MemoryWriteItem(Guid.NewGuid(), "b.md", "y", "gc-a", null, "passed"), Ct);
        await queue.FlushTurnAsync(Ct);

        var batch = sink.Commits.Single();
        Assert.Equal("gc-a, gc-b", batch.GcId);
        Assert.Equal("task-2", batch.TaskId);
        Assert.Equal("passed", batch.ValidationSummary);
    }

    [Fact]
    public async Task Flushes_are_serialized_and_each_batch_commits_once()
    {
        var sink = new RecordingBatchSink();
        var queue = new MemoryWriteQueue(_agent, sink, new InMemoryMemorySpool(), new FakeTimeProvider(_start));
        var a = Item("a.md");
        var b = Item("b.md");
        var c = Item("c.md");

        await queue.EnqueueAsync(a, Ct);
        await queue.EnqueueAsync(b, Ct);

        var gate = new TaskCompletionSource();
        sink.Gate = gate;
        var first = queue.FlushTurnAsync(Ct);
        await sink.Started.Task;

        // While the first commit is in flight, a new write arrives and a second flush starts.
        await queue.EnqueueAsync(c, Ct);
        var second = queue.FlushTurnAsync(Ct);

        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, sink.Commits.Count);
        Assert.Equal(new[] { a.WriteId, b.WriteId }, sink.Commits[0].Items.Select(i => i.WriteId));
        Assert.Equal(new[] { c.WriteId }, sink.Commits[1].Items.Select(i => i.WriteId));
        Assert.Equal(1, sink.MaxInFlight);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Commit_message_trailers_are_exact_and_deterministic()
    {
        var items = new List<MemoryWriteItem>
        {
            new(Guid.NewGuid(), "agents/agent-7/a.md", "x", "gc-9", "task-4", "passed"),
            new(Guid.NewGuid(), "agents/agent-7/b.md", "y", "gc-9", "task-4", "passed"),
        };
        var batch = MemoryBatch.Create(_agent, items);

        var first = MemoryCommitMessage.Build(batch);
        var second = MemoryCommitMessage.Build(batch);

        var expected = string.Join('\n',
            "Memory: 2 operation(s) for agent-7",
            "",
            "Bazaar-Agent: agent-7",
            "Bazaar-GC: gc-9",
            "Bazaar-Task: task-4",
            "Bazaar-Memory-Ops: 2",
            "Bazaar-Memory-Validation: passed");

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Commit_message_uses_none_when_no_item_names_gc_or_task()
    {
        var batch = MemoryBatch.Create(_agent, [new(Guid.NewGuid(), "a.md", "x", null, null, "passed")]);

        var text = MemoryCommitMessage.Build(batch);

        Assert.Contains("\nBazaar-GC: none\n", text, StringComparison.Ordinal);
        Assert.Contains("\nBazaar-Task: none\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_message_rejects_multi_line_trailer_values()
    {
        var batch = MemoryBatch.Create(_agent, [new(Guid.NewGuid(), "a.md", "x", "gc\nBazaar-Agent: evil", null, "passed")]);

        Assert.Throws<ArgumentException>(() => MemoryCommitMessage.Build(batch));
    }

    private sealed class RecordingBatchSink : IMemoryBatchSink
    {
        private readonly object _sync = new();
        private int _inFlight;

        public List<MemoryBatch> Commits { get; } = [];

        public int FailNext { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxInFlight { get; private set; }

        public async Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
        {
            lock (_sync)
            {
                _inFlight++;
                MaxInFlight = Math.Max(MaxInFlight, _inFlight);
            }

            try
            {
                Started.TrySetResult();
                var gate = Gate;
                Gate = null;
                if (gate is not null)
                {
                    await gate.Task.WaitAsync(ct);
                }

                if (FailNext > 0)
                {
                    FailNext--;
                    return MemoryCommitResult.Failure("simulated failure");
                }

                lock (_sync)
                {
                    Commits.Add(batch);
                    return MemoryCommitResult.Success($"sha-{Commits.Count}");
                }
            }
            finally
            {
                lock (_sync)
                {
                    _inFlight--;
                }
            }
        }
    }
}
