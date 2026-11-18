namespace Bazaar.Client.Athanor;

/// <summary>Why a host stopped working. <see cref="IEditorHost.Crashed"/> carries it.</summary>
public enum EditorCrashReason
{
    RendererExited,
    BridgeFault,
    Unknown,
}

/// <summary>A key event the editor surface saw, handed to the client key router before Monaco acts on it (doc 52 §7).</summary>
public sealed record EditorKeyEvent(string Key, bool Shift, bool Alt, bool Meta, bool Ctrl);

/// <summary>The content changed in the editor. <paramref name="Version"/> is a per-document counter that only rises.</summary>
public sealed record EditorChange(EditorDocumentId Id, long Version, string Content, bool Dirty);

/// <summary>
/// One editor surface, normally one window (doc 52 §3). A Monaco host, a native fallback and the headless test host
/// all implement this. A host never touches the network.
/// </summary>
public interface IEditorHost : IAsyncDisposable
{
    Task OpenAsync(EditorDocument document, CancellationToken cancellationToken);

    Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken);

    Task FocusAsync(CancellationToken cancellationToken);

    /// <summary>Applies a Monaco defineTheme payload (JSON) without reloading the editor.</summary>
    Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken);

    Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken);

    /// <summary>Shows a side-by-side diff (the single diff component, doc 41 §4.15).</summary>
    Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken);

    event EventHandler<EditorChange>? Changed;

    event EventHandler<EditorDocumentId>? SaveRequested;

    event EventHandler<EditorKeyEvent>? KeyPressed;

    event EventHandler? Ready;

    event EventHandler<EditorCrashReason>? Crashed;
}

/// <summary>Decides whether the client key router claims a key before the editor sees it (doc 52 §7).</summary>
public interface IEditorKeyHandoff
{
    /// <summary>True when the client claimed the key and the editor must not act on it.</summary>
    bool TryClaim(EditorKeyEvent key);
}
