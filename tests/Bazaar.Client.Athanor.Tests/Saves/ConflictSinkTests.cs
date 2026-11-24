using Bazaar.Client.Athanor.Saves;

namespace Bazaar.Client.Athanor.Tests.Saves;

public sealed class ConflictSinkTests : IDisposable
{
    private static readonly EditorDocumentId _id = new("ws", "src/a.cs");

    private readonly FakeFileSource _source = new();
    private readonly RecordingSink _sink = new();
    private readonly SaveCoordinator _coordinator;

    public ConflictSinkTests()
    {
        _coordinator = new SaveCoordinator(_source, new FakeDraftStore(), new ManualTimeProvider(), conflictSink: _sink);
        _coordinator.Track(new EditorDocument(_id, "original", "r1", "csharp", ReadOnly: false));
        _coordinator.OnChanged(new EditorChange(_id, 1, "mine", Dirty: true));
    }

    public void Dispose() => _coordinator.Dispose();

    [Fact]
    public async Task A_conflict_is_handed_over_once_per_server_revision_with_both_copies()
    {
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));
        _source.Enqueue(new SaveOutcome.Conflict("r8", "theirs again"));

        await _coordinator.SaveAsync(_id, CancellationToken.None);
        await _coordinator.SaveAsync(_id, CancellationToken.None);
        await _coordinator.SaveAsync(_id, CancellationToken.None);

        Assert.Equal(
            [new EditorConflictItem(_id, "r1", "mine", "r7", "theirs"), new EditorConflictItem(_id, "r1", "mine", "r8", "theirs again")],
            _sink.Opened);
    }

    [Fact]
    public async Task Keeping_mine_and_taking_theirs_are_reported_as_resolutions()
    {
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));
        await _coordinator.SaveAsync(_id, CancellationToken.None);

        await _coordinator.ResolveConflictKeepMineAsync(_id);

        Assert.Equal([(_id, EditorConflictResolution.KeptMine)], _sink.Resolved);

        _source.Enqueue(new SaveOutcome.Conflict("r9", "theirs 3"));
        await _coordinator.SaveAsync(_id, CancellationToken.None);
        await _coordinator.ResolveConflictTakeTheirsAsync(_id);

        Assert.Equal(EditorConflictResolution.TookTheirs, _sink.Resolved[^1].Item2);
    }

    [Fact]
    public async Task A_failing_sink_never_breaks_the_save()
    {
        _sink.Fail = true;
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));

        var outcome = await _coordinator.SaveAsync(_id, CancellationToken.None);

        Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.NotNull(_coordinator.GetState(_id)!.Conflict);
    }

    [Fact]
    public async Task Without_a_sink_conflicts_work_as_before()
    {
        using var plain = new SaveCoordinator(_source, new FakeDraftStore(), new ManualTimeProvider());
        plain.Track(new EditorDocument(_id, "original", "r1", "csharp", ReadOnly: false));
        plain.OnChanged(new EditorChange(_id, 1, "mine", Dirty: true));
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));

        await plain.SaveAsync(_id, CancellationToken.None);
        await plain.ResolveConflictKeepMineAsync(_id);

        Assert.Null(plain.GetState(_id)!.Conflict);
    }

    private sealed class RecordingSink : IEditorConflictSink
    {
        public List<EditorConflictItem> Opened { get; } = [];
        public List<(EditorDocumentId, EditorConflictResolution)> Resolved { get; } = [];
        public bool Fail { get; set; }

        public Task OpenedAsync(EditorConflictItem item, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("sink down");
            }

            Opened.Add(item);
            return Task.CompletedTask;
        }

        public Task ResolvedAsync(EditorDocumentId id, EditorConflictResolution resolution, CancellationToken cancellationToken)
        {
            Resolved.Add((id, resolution));
            return Task.CompletedTask;
        }
    }
}
