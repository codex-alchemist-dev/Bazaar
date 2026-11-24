using Bazaar.Client.Athanor.Saves;

namespace Bazaar.Client.Athanor.Tests.Saves;

public sealed class SaveCoordinatorTests : IDisposable
{
    private static readonly EditorDocumentId _id = new("ws", "src/a.cs");
    private static readonly TimeSpan _second = TimeSpan.FromSeconds(1);

    private readonly ManualTimeProvider _clock = new();
    private readonly FakeFileSource _source = new();
    private readonly FakeDraftStore _drafts = new();
    private readonly SaveCoordinator _coordinator;

    public SaveCoordinatorTests()
    {
        _coordinator = new SaveCoordinator(_source, _drafts, _clock);
        _coordinator.Track(new EditorDocument(_id, "original", "r1", "csharp", ReadOnly: false));
    }

    public void Dispose() => _coordinator.Dispose();

    private static EditorChange Change(long version, string content, bool dirty = true) => new(_id, version, content, dirty);

    [Fact]
    public async Task Happy_save_sends_content_with_base_revision_and_cleans_up()
    {
        _coordinator.OnChanged(Change(1, "edited"));
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);
        Assert.True(_drafts.Has(_id));
        _source.Enqueue(new SaveOutcome.Saved("r2"));

        var outcome = await _coordinator.SaveAsync(_id, CancellationToken.None);

        Assert.Equal(new SaveOutcome.Saved("r2"), outcome);
        var save = Assert.Single(_source.Saves);
        Assert.Equal(("edited", "r1"), (save.Content, save.BaseRevision));
        var state = _coordinator.GetState(_id)!;
        Assert.False(state.Dirty);
        Assert.Equal("r2", state.BaseRevision);
        Assert.False(_drafts.Has(_id));
    }

    [Fact]
    public async Task Saving_a_clean_document_does_not_call_the_server()
    {
        var outcome = await _coordinator.SaveAsync(_id, CancellationToken.None);

        Assert.Equal(new SaveOutcome.Saved("r1"), outcome);
        Assert.Empty(_source.Saves);
    }

    [Fact]
    public async Task Conflict_keeps_dirty_and_draft_and_exposes_server_copy()
    {
        _coordinator.OnChanged(Change(1, "mine"));
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));

        var outcome = await _coordinator.SaveAsync(_id, CancellationToken.None);

        Assert.IsType<SaveOutcome.Conflict>(outcome);
        var state = _coordinator.GetState(_id)!;
        Assert.True(state.Dirty);
        Assert.Equal(new SaveConflict("r7", "theirs"), state.Conflict);
        Assert.Equal("r1", state.BaseRevision);
        Assert.Equal("mine", state.Content);
        Assert.Equal("mine", (await _drafts.ReadAsync(_id, CancellationToken.None))!.Content);
    }

    [Fact]
    public async Task Unavailable_keeps_dirty_and_draft_and_exposes_reason()
    {
        _coordinator.OnChanged(Change(1, "mine"));
        _source.Enqueue(new SaveOutcome.Unavailable("offline"));

        await _coordinator.SaveAsync(_id, CancellationToken.None);

        var state = _coordinator.GetState(_id)!;
        Assert.True(state.Dirty);
        Assert.Equal("offline", state.UnavailableReason);
        Assert.Null(state.Conflict);
        Assert.True(_drafts.Has(_id));

        // A later accepted save clears the reason.
        await _coordinator.SaveAsync(_id, CancellationToken.None);
        Assert.Null(_coordinator.GetState(_id)!.UnavailableReason);
        Assert.False(_coordinator.GetState(_id)!.Dirty);
    }

    [Fact]
    public async Task Source_that_throws_is_treated_as_unavailable()
    {
        var throwing = new SaveCoordinator(new ThrowingSource(), _drafts, _clock);
        throwing.Track(new EditorDocument(_id, "x", "r1", "csharp", false));
        throwing.OnChanged(Change(1, "y"));

        var outcome = await throwing.SaveAsync(_id, CancellationToken.None);

        Assert.Equal(new SaveOutcome.Unavailable("boom"), outcome);
        Assert.True(throwing.GetState(_id)!.Dirty);
    }

    [Fact]
    public async Task Debounce_coalesces_rapid_changes_into_one_draft_write_of_the_latest_content()
    {
        _coordinator.OnChanged(Change(1, "a"));
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        _coordinator.OnChanged(Change(2, "ab"));
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        _coordinator.OnChanged(Change(3, "abc"));
        _clock.Advance(TimeSpan.FromMilliseconds(900));
        await _coordinator.DraftIdleAsync(_id);
        Assert.Empty(_drafts.Writes);

        _clock.Advance(TimeSpan.FromMilliseconds(200));
        await _coordinator.DraftIdleAsync(_id);

        var write = Assert.Single(_drafts.Writes);
        Assert.Equal("abc", write.Content);
        Assert.Equal("r1", write.BaseRevision);
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromMilliseconds(100), write.SavedAt); // fired 1 s after the last change at 1.2 s
    }

    [Fact]
    public async Task Custom_debounce_is_honoured()
    {
        using var fast = new SaveCoordinator(_source, _drafts, _clock, TimeSpan.FromMilliseconds(100));
        fast.Track(new EditorDocument(_id, "o", "r1", "csharp", false));
        fast.OnChanged(Change(1, "z"));

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        await fast.DraftIdleAsync(_id);

        Assert.Single(_drafts.Writes);
    }

    [Fact]
    public async Task Stale_draft_write_never_overwrites_a_newer_one()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drafts.HoldNextWrite = hold;
        _coordinator.OnChanged(Change(1, "first"));
        _clock.Advance(_second); // first write starts and blocks inside the store
        _coordinator.OnChanged(Change(2, "second"));
        _clock.Advance(_second); // second write queues behind it

        hold.SetResult();
        await _coordinator.DraftIdleAsync(_id);

        Assert.Equal("second", (await _drafts.ReadAsync(_id, CancellationToken.None))!.Content);
        Assert.Equal("second", _drafts.Writes[^1].Content);
    }

    [Fact]
    public async Task Stale_change_versions_are_ignored()
    {
        _coordinator.OnChanged(Change(5, "newer"));
        _coordinator.OnChanged(Change(4, "older"));

        Assert.Equal("newer", _coordinator.GetState(_id)!.Content);
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);
        Assert.Equal("newer", _drafts.Writes.Single().Content);
    }

    [Fact]
    public async Task Change_during_in_flight_save_keeps_document_dirty_on_the_new_revision()
    {
        _coordinator.OnChanged(Change(1, "v1"));
        _source.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _source.Enqueue(new SaveOutcome.Saved("r2"));
        var save = _coordinator.SaveAsync(_id, CancellationToken.None);
        await _source.Started.Task;

        _coordinator.OnChanged(Change(2, "v2"));
        _source.Hold.SetResult();
        await save;

        var state = _coordinator.GetState(_id)!;
        Assert.True(state.Dirty);
        Assert.Equal("r2", state.BaseRevision);
        Assert.Equal("v2", state.Content);
        var draft = await _drafts.ReadAsync(_id, CancellationToken.None);
        Assert.Equal(("v2", "r2"), (draft!.Content, draft.BaseRevision));
        Assert.Equal("v1", _source.Saves.Single().Content);
    }

    [Fact]
    public async Task Saves_for_one_document_never_overlap()
    {
        _coordinator.OnChanged(Change(1, "v1"));
        _source.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _coordinator.SaveAsync(_id, CancellationToken.None);
        await _source.Started.Task;
        _coordinator.OnChanged(Change(2, "v2"));
        var second = _coordinator.SaveAsync(_id, CancellationToken.None);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_source.Saves);

        _source.Hold.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _source.MaxConcurrent);
        Assert.Equal(["v1", "v2"], _source.Saves.Select(s => s.Content));
        Assert.Equal("r2", _source.Saves[1].BaseRevision);
        Assert.False(_coordinator.GetState(_id)!.Dirty);
    }

    [Fact]
    public async Task Keep_mine_rebases_onto_server_revision_and_stays_dirty()
    {
        _coordinator.OnChanged(Change(1, "mine"));
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));
        await _coordinator.SaveAsync(_id, CancellationToken.None);

        await _coordinator.ResolveConflictKeepMineAsync(_id);

        var state = _coordinator.GetState(_id)!;
        Assert.Null(state.Conflict);
        Assert.True(state.Dirty);
        Assert.Equal("r7", state.BaseRevision);
        Assert.Equal("mine", state.Content);
        Assert.Equal("r7", (await _drafts.ReadAsync(_id, CancellationToken.None))!.BaseRevision);

        await _coordinator.SaveAsync(_id, CancellationToken.None);
        Assert.Equal(("mine", "r7"), (_source.Saves[1].Content, _source.Saves[1].BaseRevision));
    }

    [Fact]
    public async Task Take_theirs_replaces_content_and_clears_dirty_and_draft()
    {
        _coordinator.OnChanged(Change(1, "mine"));
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);
        _source.Enqueue(new SaveOutcome.Conflict("r7", "theirs"));
        await _coordinator.SaveAsync(_id, CancellationToken.None);

        var content = await _coordinator.ResolveConflictTakeTheirsAsync(_id);

        Assert.Equal("theirs", content);
        var state = _coordinator.GetState(_id)!;
        Assert.Equal(("theirs", "r7", false), (state.Content, state.BaseRevision, state.Dirty));
        Assert.Null(state.Conflict);
        Assert.False(_drafts.Has(_id));
    }

    [Fact]
    public async Task Resolving_without_a_conflict_is_a_no_op()
    {
        _coordinator.OnChanged(Change(1, "mine"));

        await _coordinator.ResolveConflictKeepMineAsync(_id);
        Assert.Null(await _coordinator.ResolveConflictTakeTheirsAsync(_id));

        var state = _coordinator.GetState(_id)!;
        Assert.Equal(("mine", "r1", true), (state.Content, state.BaseRevision, state.Dirty));
    }

    [Fact]
    public async Task Recover_lists_drafts_and_a_recovered_draft_tracks_dirty_on_its_base_revision()
    {
        var other = new EditorDocumentId("ws", "b.txt");
        var draft = new EditorDraft(other, "unsaved", "r3", _clock.GetUtcNow());
        _drafts.Seed(draft);

        var found = await _coordinator.RecoverDraftsAsync(CancellationToken.None);

        Assert.Equal(draft, Assert.Single(found));
        _coordinator.Track(new EditorDocument(other, "server", "r9", "plaintext", false), draft);
        var state = _coordinator.GetState(other)!;
        Assert.Equal(("unsaved", "r3", true), (state.Content, state.BaseRevision, state.Dirty));

        _source.Enqueue(new SaveOutcome.Conflict("r9", "server"));
        var outcome = await _coordinator.SaveAsync(other, CancellationToken.None);
        Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.Equal("r3", _source.Saves.Single().BaseRevision);
    }

    [Fact]
    public async Task Draft_survives_untrack_so_it_can_be_offered_on_reopen()
    {
        _coordinator.OnChanged(Change(1, "keep"));
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);

        _coordinator.Untrack(_id);

        Assert.Equal("keep", Assert.Single(await _coordinator.RecoverDraftsAsync(CancellationToken.None)).Content);
        Assert.Null(_coordinator.GetState(_id));
    }

    [Fact]
    public async Task Reverting_to_clean_removes_the_draft()
    {
        _coordinator.OnChanged(Change(1, "x"));
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);
        Assert.True(_drafts.Has(_id));

        _coordinator.OnChanged(Change(2, "original", dirty: false));
        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);

        Assert.False(_drafts.Has(_id));
    }

    [Fact]
    public async Task Failing_draft_store_is_reported_in_state_not_thrown()
    {
        _drafts.FailWith = new IOException("disk full");
        _coordinator.OnChanged(Change(1, "x"));

        _clock.Advance(_second);
        await _coordinator.DraftIdleAsync(_id);

        Assert.Equal("disk full", _coordinator.GetState(_id)!.DraftError);
        Assert.True(_coordinator.GetState(_id)!.Dirty);
    }

    [Fact]
    public async Task Save_of_an_untracked_document_throws_and_changes_to_it_are_ignored()
    {
        var unknown = new EditorDocumentId("ws", "nope");

        _coordinator.OnChanged(new EditorChange(unknown, 1, "x", true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _coordinator.SaveAsync(unknown, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_changes_and_saves_stay_consistent()
    {
        var tasks = Enumerable.Range(1, 200).Select(i => Task.Run(() => _coordinator.OnChanged(Change(i, "c" + i))));
        await Task.WhenAll(tasks);
        await _coordinator.SaveAsync(_id, CancellationToken.None);

        var state = _coordinator.GetState(_id)!;
        // Whatever order the changes arrived in, the highest version wins.
        Assert.Equal("c200", state.Content);
        Assert.Equal("c200", _source.Saves.Single().Content);
    }

    private sealed class ThrowingSource : IEditorFileSource
    {
        public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
            Task.FromResult<EditorDocument?>(null);

        public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
