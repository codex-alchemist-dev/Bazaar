namespace Bazaar.Client.Athanor;

/// <summary>A save that ran into a newer server copy, as handed to the offline-sync conflict list (doc 57 §5 and §6, PROPOSED).</summary>
/// <param name="Id">The document.</param>
/// <param name="BaseRevision">The revision the user's edit started from.</param>
/// <param name="MyContent">The user's content, which the server did not take.</param>
/// <param name="ServerRevision">The server's current revision.</param>
/// <param name="ServerContent">The server's current content.</param>
public sealed record EditorConflictItem(EditorDocumentId Id, string BaseRevision, string MyContent, string ServerRevision, string ServerContent);

/// <summary>How the user settled a conflict.</summary>
public enum EditorConflictResolution
{
    /// <summary>The user's text was kept and rebased onto the server revision.</summary>
    KeptMine,

    /// <summary>The server copy replaced the user's text.</summary>
    TookTheirs,
}

/// <summary>
/// Receives conflicts so a conflict list (the Vault offline-sync module, doc 18) can show them next to the editor's own diff.
/// The editor never waits on a sink and ignores its failures; a sink that is absent changes nothing.
/// </summary>
public interface IEditorConflictSink
{
    /// <summary>A save was refused because the server moved on. Called once per distinct server revision.</summary>
    Task OpenedAsync(EditorConflictItem item, CancellationToken cancellationToken);

    /// <summary>The user settled the conflict in the editor.</summary>
    Task ResolvedAsync(EditorDocumentId id, EditorConflictResolution resolution, CancellationToken cancellationToken);
}
