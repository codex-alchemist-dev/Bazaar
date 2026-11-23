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

    /// <summary>
    /// Creates a new document in an existing folder, never over an existing file (doc 57 section 8). The default reports that the
    /// source cannot create, so a read-only or offline source needs no code.
    /// </summary>
    Task<CreateOutcome> CreateAsync(EditorDocumentId id, string content, CancellationToken cancellationToken) =>
        Task.FromResult<CreateOutcome>(new CreateOutcome.Unavailable("This source cannot create documents."));
}
