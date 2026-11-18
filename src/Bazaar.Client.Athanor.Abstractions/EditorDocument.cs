namespace Bazaar.Client.Athanor;

/// <summary>Where a document lives: a workspace the server controls and a normalised path inside it (doc 57 §5).</summary>
/// <param name="WorkspaceId">The workspace id.</param>
/// <param name="Path">Forward-slash path relative to the workspace root, with no <c>.</c> or <c>..</c> segments.</param>
public readonly record struct EditorDocumentId(string WorkspaceId, string Path)
{
    public override string ToString() => $"{WorkspaceId}:{Path}";
}

/// <summary>A document as the editor opens it. <paramref name="Revision"/> is the server revision the content came from.</summary>
public sealed record EditorDocument(
    EditorDocumentId Id,
    string Content,
    string Revision,
    string LanguageId,
    bool ReadOnly);

/// <summary>A save result (doc 57 §5). A conflict carries the server copy so the editor can show a diff.</summary>
public abstract record SaveOutcome
{
    private SaveOutcome() { }

    /// <summary>The server accepted the save and returned the new revision.</summary>
    public sealed record Saved(string NewRevision) : SaveOutcome;

    /// <summary>The server revision moved on. <paramref name="ServerContent"/> is its current content.</summary>
    public sealed record Conflict(string ServerRevision, string ServerContent) : SaveOutcome;

    /// <summary>The server cannot be reached or refused the write. The draft stays.</summary>
    public sealed record Unavailable(string Reason) : SaveOutcome;
}
