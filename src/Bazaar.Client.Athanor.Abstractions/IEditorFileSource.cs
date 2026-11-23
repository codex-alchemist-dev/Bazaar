namespace Bazaar.Client.Athanor;

/// <summary>
/// Reads and writes workspace files through the company server. The client holds no repository credential (doc 28, doc 21).
/// Implementations validate nothing the server also validates, but must never be handed an unnormalised path.
/// </summary>
public interface IEditorFileSource
{
    /// <summary>Loads a document, or null when it does not exist.</summary>
    Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken);

    /// <summary>Saves content against the revision that was opened. Never overwrites a newer revision.</summary>
    Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken);
}
