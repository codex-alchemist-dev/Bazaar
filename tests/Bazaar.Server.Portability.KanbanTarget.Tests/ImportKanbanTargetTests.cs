using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.KanbanTarget.Tests;

public class ImportKanbanTargetTests
{
    private static readonly Guid _project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class FakeGcSink : IImportGcSink
    {
        public List<(string JobId, string Source)> GcsCreated { get; } = [];
        public List<(Guid GcId, string Text, Guid MessageId)> Messages { get; } = [];
        private readonly Dictionary<string, Guid> _gcByJob = [];

        public Task<Guid> EnsureJobGcAsync(string jobId, string sourceName, CancellationToken ct)
        {
            if (!_gcByJob.TryGetValue(jobId, out var gc))
            {
                gc = Guid.NewGuid();
                _gcByJob[jobId] = gc;
                GcsCreated.Add((jobId, sourceName));
            }
            return Task.FromResult(gc);
        }

        public Task<Guid> AppendMessageAsync(Guid gcId, string text, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            Messages.Add((gcId, text, id));
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<string>> ReadJobMessagesAsync(string jobId, CancellationToken ct)
        {
            var gc = _gcByJob.TryGetValue(jobId, out var g) ? g : Guid.Empty;
            return Task.FromResult<IReadOnlyList<string>>(Messages.Where(m => m.GcId == gc).Select(m => m.Text).ToList());
        }
    }

    private sealed class FakeStore(bool refuse = false) : IImportTaskStore
    {
        public List<(Guid ProjectId, Guid GcId, Guid MessageId, string Title)> Created { get; } = [];
        public Task<Guid?> CreateFromMessageAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct)
        {
            if (refuse) return Task.FromResult<Guid?>(null);
            Created.Add((projectId, gcId, messageId, title));
            return Task.FromResult<Guid?>(Guid.NewGuid());
        }
    }

    private static ImportItem Item(string key, string title, string column = "Ready", string board = "Launch board") =>
        new(
            new SourceRecord(key, ImportCommitter.KanbanTaskKind, title, "card body",
                new Dictionary<string, string> { ["kanban_column"] = column, ["board"] = board }),
            new ImportProvenance("job-1", key));

    [Fact]
    public async Task Creates_one_import_gc_per_job_named_after_the_source()
    {
        var gc = new FakeGcSink();
        var target = new ImportKanbanTarget(gc, new FakeStore(), _project);

        await target.AppendBatchAsync("job-1", 0, [Item("a", "A"), Item("b", "B")], CancellationToken.None);
        await target.AppendBatchAsync("job-1", 1, [Item("c", "C")], CancellationToken.None);

        var created = Assert.Single(gc.GcsCreated);
        Assert.Equal("job-1", created.JobId);
        Assert.Equal("Launch board", created.Source);
    }

    [Fact]
    public async Task Each_item_becomes_a_message_then_a_task_pinned_to_it()
    {
        var gc = new FakeGcSink();
        var store = new FakeStore();
        var target = new ImportKanbanTarget(gc, store, _project);

        await target.AppendBatchAsync("job-1", 0, [Item("a", "Write spec"), Item("b", "Ship it")], CancellationToken.None);

        Assert.Equal(2, gc.Messages.Count);
        Assert.Equal(2, store.Created.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(_project, store.Created[i].ProjectId);
            Assert.Equal(gc.Messages[i].GcId, store.Created[i].GcId);
            Assert.Equal(gc.Messages[i].MessageId, store.Created[i].MessageId);
        }
        Assert.Equal("Write spec", store.Created[0].Title);
    }

    [Fact]
    public async Task Message_text_carries_the_source_key_marker_and_the_source_column()
    {
        var gc = new FakeGcSink();
        var target = new ImportKanbanTarget(gc, new FakeStore(), _project);

        await target.AppendBatchAsync("job-1", 0, [Item("card-42", "Write spec", column: "In progress")], CancellationToken.None);

        var text = Assert.Single(gc.Messages).Text;
        Assert.StartsWith("[import:card-42]", text, StringComparison.Ordinal);
        Assert.Contains("source column: In progress", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_source_keys_returns_only_keys_from_this_jobs_markers()
    {
        var gc = new FakeGcSink();
        var target = new ImportKanbanTarget(gc, new FakeStore(), _project);
        await target.AppendBatchAsync("job-1", 0, [Item("a", "A"), Item("b", "B")], CancellationToken.None);
        await target.AppendBatchAsync("job-2", 0, [Item("z", "Z")], CancellationToken.None);

        var keys = await target.ReadSourceKeysAsync("job-1", CancellationToken.None);

        Assert.Equal(["a", "b"], keys);
    }

    [Fact]
    public async Task A_refused_task_fails_the_batch()
    {
        var target = new ImportKanbanTarget(new FakeGcSink(), new FakeStore(refuse: true), _project);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            target.AppendBatchAsync("job-1", 0, [Item("a", "A")], CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_batch_creates_no_gc()
    {
        var gc = new FakeGcSink();
        var target = new ImportKanbanTarget(gc, new FakeStore(), _project);

        await target.AppendBatchAsync("job-1", 0, [], CancellationToken.None);

        Assert.Empty(gc.GcsCreated);
    }
}
