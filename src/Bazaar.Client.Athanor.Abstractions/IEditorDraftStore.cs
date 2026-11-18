namespace Bazaar.Client.Athanor;

/// <summary>A locally kept unsaved edit (doc 28 renderer crash, doc 52 §5).</summary>
public sealed record EditorDraft(EditorDocumentId Id, string Content, string BaseRevision, DateTimeOffset SavedAt);

/// <summary>Holds dirty content until the server accepts it, so a crash or restart loses nothing.</summary>
public interface IEditorDraftStore
{
    Task WriteAsync(EditorDraft draft, CancellationToken cancellationToken);

    Task<EditorDraft?> ReadAsync(EditorDocumentId id, CancellationToken cancellationToken);

    /// <summary>Removes the draft after a save was accepted. Removing a missing draft is not an error.</summary>
    Task DeleteAsync(EditorDocumentId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<EditorDraft>> ListAsync(CancellationToken cancellationToken);
}
