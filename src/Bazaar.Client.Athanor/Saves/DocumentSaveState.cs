namespace Bazaar.Client.Athanor.Saves;

/// <summary>The server copy a rejected save ran into (doc 57 §5). The editor shows a diff against it.</summary>
public sealed record SaveConflict(string ServerRevision, string ServerContent);

/// <summary>A point-in-time view of one tracked document, for the UI.</summary>
/// <param name="Id">The document.</param>
/// <param name="BaseRevision">The server revision the next save is sent against.</param>
/// <param name="Content">The latest content the editor reported.</param>
/// <param name="Dirty">True while the content is not accepted by the server.</param>
/// <param name="Conflict">Set after a rejected save until the user resolves it.</param>
/// <param name="UnavailableReason">Set after a save the server could not take; cleared by the next accepted save.</param>
/// <param name="DraftError">Set when the last local draft write failed; cleared by the next one that works.</param>
public sealed record DocumentSaveState(
    EditorDocumentId Id,
    string BaseRevision,
    string Content,
    bool Dirty,
    SaveConflict? Conflict,
    string? UnavailableReason,
    string? DraftError);
