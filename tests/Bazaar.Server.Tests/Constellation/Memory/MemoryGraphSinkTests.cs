using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Tests.Memory.Notes;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>The write-path sink: a successful Vault commit projects and stores its notes, and nothing here fails the commit.</summary>
public sealed class MemoryGraphSinkTests
{
    private const string Agent = "alpha";
    private const string PathA = "agents/alpha/memory/lessons/retry-backoff.md";
    private const string PathB = "agents/alpha/memory/lessons/second.md";
    private const string SecondId = "01J9ZABCDEFGHJKMNPQRSTVWXZ";

    [Fact]
    public async Task Successful_commit_stores_one_revision_for_the_note()
    {
        var inner = new FakeSink(MemoryCommitResult.Success("sha-1"));
        var store = new FakeStore();
        var sink = new MemoryGraphSink(inner, store, new CapturingLogger());

        await sink.CommitAsync(Batch(Item(PathA, NoteFixtures.Text())), CancellationToken.None);

        var revision = Assert.Single(store.Stored);
        Assert.Equal(NoteFixtures.NoteId, revision.NoteId);
        Assert.Equal(1, revision.Version);
        Assert.Equal(Agent, revision.ScopeAgentId);
        Assert.Equal(PathA, revision.VaultPath);
        Assert.Equal(1, inner.CommitCount);
    }

    [Fact]
    public async Task Inner_result_is_returned_unchanged()
    {
        var expected = MemoryCommitResult.Success("sha-2");
        var sink = new MemoryGraphSink(new FakeSink(expected), new FakeStore(), new CapturingLogger());

        var result = await sink.CommitAsync(Batch(Item(PathA, NoteFixtures.Text())), CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task Failed_inner_commit_stores_nothing()
    {
        var store = new FakeStore();
        var failure = MemoryCommitResult.Failure("disk full");
        var sink = new MemoryGraphSink(new FakeSink(failure), store, new CapturingLogger());

        var result = await sink.CommitAsync(Batch(Item(PathA, NoteFixtures.Text())), CancellationToken.None);

        Assert.Same(failure, result);
        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Unparsable_note_is_skipped_and_the_commit_still_succeeds()
    {
        var store = new FakeStore();
        var logger = new CapturingLogger();
        var sink = new MemoryGraphSink(new FakeSink(MemoryCommitResult.Success("sha-3")), store, logger);

        var result = await sink.CommitAsync(
            Batch(Item(PathA, "no front matter here"), Item(PathB, NoteFixtures.Text(NoteWithId(SecondId)))),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var revision = Assert.Single(store.Stored);
        Assert.Equal(SecondId, revision.NoteId);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Store_exception_for_one_item_is_logged_and_the_other_items_still_store()
    {
        var store = new FakeStore
        {
            OnStore = revision =>
            {
                if (revision.NoteId == NoteFixtures.NoteId)
                {
                    throw new InvalidOperationException("db down");
                }
            },
        };
        var logger = new CapturingLogger();
        var sink = new MemoryGraphSink(new FakeSink(MemoryCommitResult.Success("sha-4")), store, logger);

        var result = await sink.CommitAsync(
            Batch(Item(PathA, NoteFixtures.Text()), Item(PathB, NoteFixtures.Text(NoteWithId(SecondId)))),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var stored = Assert.Single(store.Stored);
        Assert.Equal(SecondId, stored.NoteId);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task Note_outside_the_writer_memory_area_is_not_projected()
    {
        var store = new FakeStore();
        var sink = new MemoryGraphSink(new FakeSink(MemoryCommitResult.Success("sha-5")), store, new CapturingLogger());

        await sink.CommitAsync(
            Batch(Item("gc/notes/shared.md", NoteFixtures.Text())),
            CancellationToken.None);

        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Wrap_returns_a_decorator_that_projects_and_returns_the_inner_result()
    {
        var expected = MemoryCommitResult.Success("sha-6");
        var store = new FakeStore();
        var sink = MemoryGraphSink.Wrap(new FakeSink(expected), store, new CapturingLogger());

        Assert.IsType<MemoryGraphSink>(sink);
        var result = await sink.CommitAsync(Batch(Item(PathA, NoteFixtures.Text())), CancellationToken.None);

        Assert.Same(expected, result);
        var revision = Assert.Single(store.Stored);
        Assert.Equal(NoteFixtures.NoteId, revision.NoteId);
    }

    private static MemoryBatch Batch(params MemoryWriteItem[] items) => MemoryBatch.Create(Agent, items);

    private static MemoryWriteItem Item(string path, string content) =>
        new(Guid.NewGuid(), path, content, null, null, "ok");

    private static MemoryNote NoteWithId(string id) => NoteFixtures.Note() with { Id = id };

    private sealed class FakeSink(MemoryCommitResult result) : IMemoryBatchSink
    {
        public int CommitCount { get; private set; }

        public Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
        {
            CommitCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeStore : IMemoryGraphStore
    {
        public List<MemoryGraphRevision> Stored { get; } = [];

        /// <summary>Runs for each store call before it is recorded. Throw here to simulate a store failure.</summary>
        public Action<MemoryGraphRevision>? OnStore { get; init; }

        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct)
        {
            OnStore?.Invoke(revision);
            Stored.Add(revision);
            return Task.CompletedTask;
        }

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<MemoryGraphSink>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
