namespace Bazaar.Contracts.Athanor;

/// <summary>Payload of <c>editor.open</c> (doc 57 §6, PROPOSED): one file in a server-controlled workspace.</summary>
/// <param name="WorkspaceId">The workspace id.</param>
/// <param name="Path">Forward-slash path relative to the workspace root.</param>
public sealed record EditorOpenRequest(string WorkspaceId, string Path);

/// <summary>Reply to <c>editor.open</c>.</summary>
/// <param name="Content">The file text.</param>
/// <param name="Revision">Opaque server revision of the content.</param>
/// <param name="LanguageHint">Optional language id the server suggests.</param>
/// <param name="ReadOnly">True when the file must open read-only (size limit or read-only workspace).</param>
/// <param name="SizeBytes">Size of the file in bytes.</param>
public sealed record EditorOpenReply(string Content, string Revision, string? LanguageHint, bool ReadOnly, long SizeBytes);

/// <summary>Payload of <c>editor.save</c>: the content, and the revision it was opened from.</summary>
/// <param name="WorkspaceId">The workspace id.</param>
/// <param name="Path">Forward-slash path relative to the workspace root.</param>
/// <param name="BaseRevision">The revision the edit started from.</param>
/// <param name="Content">The full new content.</param>
public sealed record EditorSaveRequest(string WorkspaceId, string Path, string BaseRevision, string Content);

/// <summary>
/// Reply to <c>editor.save</c>. Accepted: <paramref name="NewRevision"/> is set. Revision moved on: the server may answer
/// with <paramref name="ServerRevision"/> and <paramref name="ServerContent"/> and no new revision, because the wire error
/// envelope has no room for them (doc 57 §6, PROPOSED).
/// </summary>
/// <param name="NewRevision">The revision after an accepted save.</param>
/// <param name="ServerRevision">With a conflict: the server's current revision.</param>
/// <param name="ServerContent">With a conflict: the server's current content.</param>
public sealed record EditorSaveReply(string? NewRevision, string? ServerRevision, string? ServerContent);
