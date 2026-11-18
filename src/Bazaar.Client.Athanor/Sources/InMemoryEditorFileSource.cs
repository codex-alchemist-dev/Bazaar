using System.Globalization;

namespace Bazaar.Client.Athanor.Sources;

/// <summary>
/// An <see cref="IEditorFileSource"/> that keeps documents in memory (doc 52 §3, §5). It backs tests and any
/// read-only sample workspace. Every member takes one lock, so it is safe to share across threads. Revisions come from
/// one counter, so they only rise. A save is accepted only when its base revision is still current; otherwise it returns
/// a conflict that carries the current server content. Saves to a read-only document, or to a document that was never
/// seeded, are refused as unavailable.
/// </summary>
public sealed class InMemoryEditorFileSource : IEditorFileSource
{
    private readonly object _gate = new();
    private readonly Dictionary<EditorDocumentId, Entry> _documents = [];
    private long _lastRevision;

    /// <summary>Adds or replaces a document. The seeded copy gets a new revision, so an older opened copy conflicts on save.</summary>
    public void Seed(EditorDocumentId id, string content, string languageId, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(languageId);

        lock (_gate)
        {
            _documents[id] = new Entry(content, languageId, NextRevision(), readOnly);
        }
    }

    public Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(id, out Entry? entry))
            {
                return Task.FromResult<EditorDocument?>(null);
            }

            return Task.FromResult<EditorDocument?>(
                new EditorDocument(id, entry.Content, entry.Revision, entry.LanguageId, entry.ReadOnly));
        }
    }

    public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(baseRevision);

        SaveOutcome outcome;
        lock (_gate)
        {
            if (!_documents.TryGetValue(id, out Entry? entry))
            {
                outcome = new SaveOutcome.Unavailable("document does not exist");
            }
            else if (entry.ReadOnly)
            {
                outcome = new SaveOutcome.Unavailable("document is read-only");
            }
            else if (entry.Revision != baseRevision)
            {
                outcome = new SaveOutcome.Conflict(entry.Revision, entry.Content);
            }
            else
            {
                string revision = NextRevision();
                _documents[id] = entry with { Content = content, Revision = revision };
                outcome = new SaveOutcome.Saved(revision);
            }
        }

        return Task.FromResult(outcome);
    }

    // Callers hold _gate.
    private string NextRevision() => (++_lastRevision).ToString(CultureInfo.InvariantCulture);

    private sealed record Entry(string Content, string LanguageId, string Revision, bool ReadOnly);
}
