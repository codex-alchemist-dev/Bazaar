namespace Bazaar.Client.Athanor.Workspace;

/// <summary>
/// One open tab as the workspace reports it (doc 57 §3). Immutable: a new snapshot is built on every change, so a
/// caller that holds a tab keeps a consistent view.
/// </summary>
/// <param name="Id">The document the tab shows.</param>
/// <param name="Title">The last path segment of <see cref="EditorDocumentId.Path"/>.</param>
/// <param name="Dirty">True while the editor holds edits the server has not accepted.</param>
/// <param name="ReadOnly">True when the document opened read-only (too large, or the source said so).</param>
/// <param name="LanguageId">The language the document opened with.</param>
public sealed record EditorTab(EditorDocumentId Id, string Title, bool Dirty, bool ReadOnly, string LanguageId);
