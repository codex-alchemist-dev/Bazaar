using System.Collections.Concurrent;

namespace Bazaar.Client.Athanor.Saves;

/// <summary>
/// Owns save and draft behaviour for open documents (doc 52 §5): revision-checked saves that never overwrite,
/// and a debounced local draft that stays until the server accepts the content. Safe to call from any thread.
/// </summary>
public sealed class SaveCoordinator : IDisposable
{
    /// <summary>Default draft debounce (doc 52 §5).</summary>
    public static readonly TimeSpan DefaultDraftDebounce = TimeSpan.FromSeconds(1);

    private readonly IEditorFileSource _source;
    private readonly IEditorDraftStore _drafts;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _draftDebounce;
    private readonly ConcurrentDictionary<EditorDocumentId, DocumentEntry> _documents = new();

    public SaveCoordinator(
        IEditorFileSource source,
        IEditorDraftStore drafts,
        TimeProvider clock,
        TimeSpan? draftDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(clock);
        _source = source;
        _drafts = drafts;
        _clock = clock;
        _draftDebounce = draftDebounce ?? DefaultDraftDebounce;
    }

    /// <summary>Raised after a document's state changed (outside any internal lock).</summary>
    public event EventHandler<EditorDocumentId>? StateChanged;

    /// <summary>
    /// Starts tracking an opened document. To apply a draft offered by <see cref="RecoverDraftsAsync"/>, pass it as
    /// <paramref name="recovered"/>: the document then starts dirty with the draft's content and base revision, so a
    /// save against a newer server revision conflicts instead of overwriting.
    /// </summary>
    public void Track(EditorDocument document, EditorDraft? recovered = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entry = recovered is null
            ? new DocumentEntry(document.Id, document.Revision, document.Content, dirty: false)
            : new DocumentEntry(document.Id, recovered.BaseRevision, recovered.Content, dirty: true);
        if (_documents.TryRemove(document.Id, out var old))
        {
            old.DisposeTimer();
        }

        _documents[document.Id] = entry;
        Raise(document.Id);
    }

    /// <summary>Stops tracking a closed document. A kept draft stays on disk so it can be offered on reopen.</summary>
    public void Untrack(EditorDocumentId id)
    {
        if (_documents.TryRemove(id, out var entry))
        {
            entry.DisposeTimer();
        }
    }

    /// <summary>Returns the current state, or null for an untracked document.</summary>
    public DocumentSaveState? GetState(EditorDocumentId id) =>
        _documents.TryGetValue(id, out var entry) ? entry.Snapshot() : null;

    /// <summary>
    /// Records the editor's latest content and (re)starts the draft debounce. Changes with a version that is not
    /// higher than the last seen one are stale and ignored. Untracked documents are ignored.
    /// </summary>
    public void OnChanged(EditorChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!_documents.TryGetValue(change.Id, out var entry))
        {
            return;
        }

        lock (entry.Gate)
        {
            if (change.Version <= entry.Version)
            {
                return;
            }

            entry.Version = change.Version;
            entry.Content = change.Content;
            entry.Dirty = change.Dirty;
            entry.Timer ??= _clock.CreateTimer(_ => OnDebounceElapsed(entry), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            entry.Timer.Change(_draftDebounce, Timeout.InfiniteTimeSpan);
        }

        Raise(change.Id);
    }

    /// <summary>
    /// Sends the latest content with the base revision. One save per document runs at a time. A clean document
    /// returns <see cref="SaveOutcome.Saved"/> with its current revision without calling the server.
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        var entry = GetEntry(id);
        await entry.SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string content;
            string baseRevision;
            long sentVersion;
            lock (entry.Gate)
            {
                if (!entry.Dirty)
                {
                    return new SaveOutcome.Saved(entry.BaseRevision);
                }

                content = entry.Content;
                baseRevision = entry.BaseRevision;
                sentVersion = entry.Version;
            }

            SaveOutcome outcome;
            try
            {
                outcome = await _source.SaveAsync(id, content, baseRevision, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A source that throws is treated like an unreachable server: keep dirty and keep the draft.
                outcome = new SaveOutcome.Unavailable(ex.Message);
            }

            lock (entry.Gate)
            {
                switch (outcome)
                {
                    case SaveOutcome.Saved saved:
                        entry.BaseRevision = saved.NewRevision;
                        entry.Conflict = null;
                        entry.UnavailableReason = null;
                        // An edit made while the save ran is not covered by it: stay dirty on the new revision.
                        if (entry.Version == sentVersion)
                        {
                            entry.Dirty = false;
                        }

                        break;
                    case SaveOutcome.Conflict conflict:
                        entry.Conflict = new SaveConflict(conflict.ServerRevision, conflict.ServerContent);
                        entry.UnavailableReason = null;
                        break;
                    case SaveOutcome.Unavailable unavailable:
                        entry.UnavailableReason = unavailable.Reason;
                        break;
                }
            }

            // Saved: delete the draft, or rewrite it when edits arrived during the save. Otherwise the draft stays,
            // refreshed so it holds everything typed so far.
            await SyncDraftAsync(entry).ConfigureAwait(false);
            Raise(id);
            return outcome;
        }
        finally
        {
            entry.SaveGate.Release();
        }
    }

    /// <summary>
    /// Keeps the user's content and rebases it onto the server revision from the conflict. Still dirty; the next
    /// save is sent against the server revision. No-op without a conflict.
    /// </summary>
    public async Task ResolveConflictKeepMineAsync(EditorDocumentId id)
    {
        var entry = GetEntry(id);
        lock (entry.Gate)
        {
            if (entry.Conflict is null)
            {
                return;
            }

            entry.BaseRevision = entry.Conflict.ServerRevision;
            entry.Conflict = null;
            entry.Dirty = true;
        }

        await SyncDraftAsync(entry).ConfigureAwait(false);
        Raise(id);
    }

    /// <summary>
    /// Discards the user's content for the server copy: content replaced, document clean, draft deleted.
    /// Returns the content the editor should now show, or null when there was no conflict.
    /// </summary>
    public async Task<string?> ResolveConflictTakeTheirsAsync(EditorDocumentId id)
    {
        var entry = GetEntry(id);
        string content;
        lock (entry.Gate)
        {
            if (entry.Conflict is null)
            {
                return null;
            }

            content = entry.Conflict.ServerContent;
            entry.BaseRevision = entry.Conflict.ServerRevision;
            entry.Content = content;
            entry.Conflict = null;
            entry.UnavailableReason = null;
            entry.Dirty = false;
        }

        await SyncDraftAsync(entry).ConfigureAwait(false);
        Raise(id);
        return content;
    }

    /// <summary>Lists local drafts so the caller can offer them back when the documents are reopened.</summary>
    public Task<IReadOnlyList<EditorDraft>> RecoverDraftsAsync(CancellationToken cancellationToken) =>
        _drafts.ListAsync(cancellationToken);

    /// <summary>Completes when the draft write that is currently scheduled or running for a document has finished.</summary>
    public Task DraftIdleAsync(EditorDocumentId id)
    {
        if (!_documents.TryGetValue(id, out var entry))
        {
            return Task.CompletedTask;
        }

        lock (entry.Gate)
        {
            return entry.PendingDraft;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _documents.Values)
        {
            entry.DisposeTimer();
        }

        _documents.Clear();
    }

    private DocumentEntry GetEntry(EditorDocumentId id) =>
        _documents.TryGetValue(id, out var entry)
            ? entry
            : throw new InvalidOperationException($"Document {id} is not tracked.");

    private void OnDebounceElapsed(DocumentEntry entry)
    {
        lock (entry.Gate)
        {
            // Chained so draft writes for one document never overlap and run in order.
            entry.PendingDraft = RunDraftAfterAsync(entry.PendingDraft, entry);
        }
    }

    private async Task RunDraftAfterAsync(Task previous, DocumentEntry entry)
    {
        await previous.ConfigureAwait(false);
        await SyncDraftAsync(entry).ConfigureAwait(false);
        Raise(entry.Id);
    }

    /// <summary>
    /// Brings the draft file in line with the state as it is when the draft gate is entered: a dirty document writes
    /// its latest content, a clean one removes the draft. Taking the snapshot inside the gate means a write that
    /// queued earlier can never put older content over a newer draft.
    /// </summary>
    private async Task SyncDraftAsync(DocumentEntry entry)
    {
        await entry.DraftGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool dirty;
            string content;
            string baseRevision;
            lock (entry.Gate)
            {
                dirty = entry.Dirty;
                content = entry.Content;
                baseRevision = entry.BaseRevision;
            }

            try
            {
                if (dirty)
                {
                    await _drafts.WriteAsync(new EditorDraft(entry.Id, content, baseRevision, _clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _drafts.DeleteAsync(entry.Id, CancellationToken.None).ConfigureAwait(false);
                }

                lock (entry.Gate)
                {
                    entry.DraftError = null;
                }
            }
            catch (Exception ex)
            {
                // A failing draft store must not take the editor down; surface it in the state instead.
                lock (entry.Gate)
                {
                    entry.DraftError = ex.Message;
                }
            }
        }
        finally
        {
            entry.DraftGate.Release();
        }
    }

    private void Raise(EditorDocumentId id) => StateChanged?.Invoke(this, id);

    private sealed class DocumentEntry
    {
        public DocumentEntry(EditorDocumentId id, string baseRevision, string content, bool dirty)
        {
            Id = id;
            BaseRevision = baseRevision;
            Content = content;
            Dirty = dirty;
        }

        public EditorDocumentId Id { get; }

        /// <summary>Guards every mutable field below; never held across an await.</summary>
        public object Gate { get; } = new();

        /// <summary>One save at a time per document.</summary>
        public SemaphoreSlim SaveGate { get; } = new(1, 1);

        /// <summary>One draft file operation at a time per document.</summary>
        public SemaphoreSlim DraftGate { get; } = new(1, 1);

        public string BaseRevision { get; set; }

        public string Content { get; set; }

        public bool Dirty { get; set; }

        public long Version { get; set; }

        public SaveConflict? Conflict { get; set; }

        public string? UnavailableReason { get; set; }

        public string? DraftError { get; set; }

        public ITimer? Timer { get; set; }

        public Task PendingDraft { get; set; } = Task.CompletedTask;

        public DocumentSaveState Snapshot()
        {
            lock (Gate)
            {
                return new DocumentSaveState(Id, BaseRevision, Content, Dirty, Conflict, UnavailableReason, DraftError);
            }
        }

        public void DisposeTimer()
        {
            lock (Gate)
            {
                Timer?.Dispose();
                Timer = null;
            }
        }
    }
}
