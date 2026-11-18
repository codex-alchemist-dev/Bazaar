using System.Collections.Concurrent;

namespace Bazaar.Client.Athanor.Drafts;

/// <summary>
/// A thread-safe draft store that keeps everything in memory. Used by tests and by hosts that must not touch disk.
/// The draft key is the workspace id and path only; the revision is not part of the key (doc 57 §5).
/// <see cref="EditorDraft.SavedAt"/> is stamped from the store's clock on write.
/// </summary>
public sealed class InMemoryEditorDraftStore : IEditorDraftStore
{
    private readonly ConcurrentDictionary<EditorDocumentId, EditorDraft> _drafts = new();
    private readonly TimeProvider _clock;

    public InMemoryEditorDraftStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task WriteAsync(EditorDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _drafts[draft.Id] = draft with { SavedAt = _clock.GetUtcNow() };
        return Task.CompletedTask;
    }

    public Task<EditorDraft?> ReadAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_drafts.TryGetValue(Normalise(id), out var draft) ? draft : null);
    }

    public Task DeleteAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _drafts.TryRemove(Normalise(id), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EditorDraft>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EditorDraft> list = _drafts.Values
            .OrderBy(d => d.SavedAt)
            .ThenBy(d => d.Id.WorkspaceId, StringComparer.Ordinal)
            .ThenBy(d => d.Id.Path, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    // Keys ignore the revision, so a draft written against one revision is keyed the same as one written against another.
    private static EditorDocumentId Normalise(EditorDocumentId id) => new(id.WorkspaceId, id.Path);
}
