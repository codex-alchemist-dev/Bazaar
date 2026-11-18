namespace Bazaar.Client.Athanor.Hosting;

/// <summary>The last diff the host was asked to show (doc 57 §3).</summary>
public sealed record PlainTextDiffRequest(string Original, string Modified, string LanguageId);

/// <summary>
/// An <see cref="IEditorHost"/> with in-memory buffers and no real editor surface (doc 57 §3). It stands in for Monaco in
/// headless tests, on Linux before its spike, and as the failure fallback if S12 fails. It never touches the network.
/// Single-threaded, like the UI host it replaces. Surface operations throw while the host is crashed, until
/// <see cref="SimulateReady"/> is called. Buffers survive a simulated crash so tests can inspect them.
/// </summary>
public sealed class PlainTextFallbackHost : IEditorHost
{
    private readonly IEditorKeyHandoff? _handoff;
    private readonly Dictionary<EditorDocumentId, OpenBuffer> _buffers = [];

    // Per-document version counters survive close and reopen so the counter only ever rises (doc 57 §3).
    private readonly Dictionary<EditorDocumentId, long> _versions = [];
    private bool _readyRaised;
    private bool _crashed;
    private bool _disposed;

    /// <param name="handoff">The client key router. Keys it claims never reach <see cref="KeyPressed"/>. Null means no claims.</param>
    public PlainTextFallbackHost(IEditorKeyHandoff? handoff = null)
    {
        _handoff = handoff;
    }

    /// <summary>The Monaco defineTheme JSON pushed last, or null when no theme was pushed.</summary>
    public string? ThemeJson { get; private set; }

    /// <summary>The diff shown last, or null when none was shown.</summary>
    public PlainTextDiffRequest? LastDiffRequest { get; private set; }

    /// <summary>True between <see cref="SimulateCrash"/> and <see cref="SimulateReady"/>.</summary>
    public bool IsCrashed => _crashed;

    public event EventHandler<EditorChange>? Changed;

    public event EventHandler<EditorDocumentId>? SaveRequested;

    public event EventHandler<EditorKeyEvent>? KeyPressed;

    public event EventHandler? Ready;

    public event EventHandler<EditorCrashReason>? Crashed;

    public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Run(() =>
        {
            EnsureSurfaceUsable();
            _buffers[document.Id] = new OpenBuffer(document.Content, document.ReadOnly);
            _versions.TryAdd(document.Id, 0);
            if (!_readyRaised)
            {
                _readyRaised = true;
                Ready?.Invoke(this, EventArgs.Empty);
            }
        }, cancellationToken);
    }

    public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) =>
        Run(() =>
        {
            EnsureNotDisposed();
            _buffers.Remove(id);
        }, cancellationToken);

    public Task FocusAsync(CancellationToken cancellationToken) =>
        Run(EnsureSurfaceUsable, cancellationToken);

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        return Run(() =>
        {
            EnsureSurfaceUsable();
            ThemeJson = monacoThemeJson;
        }, cancellationToken);
    }

    public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) =>
        Run(() =>
        {
            EnsureSurfaceUsable();
            OpenBuffer buffer = FindBuffer(id);
            buffer.ReadOnly = isReadOnly;
        }, cancellationToken);

    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(languageId);
        return Run(() =>
        {
            EnsureSurfaceUsable();
            LastDiffRequest = new PlainTextDiffRequest(original, modified, languageId);
        }, cancellationToken);
    }

    /// <summary>The buffer content of an open document. Works while crashed, so tests can inspect it.</summary>
    public string GetContent(EditorDocumentId id)
    {
        EnsureNotDisposed();
        return FindBuffer(id).Content;
    }

    /// <summary>True when the open document differs from the content it was opened with.</summary>
    public bool IsDirty(EditorDocumentId id)
    {
        EnsureNotDisposed();
        return FindBuffer(id).IsDirty;
    }

    /// <summary>
    /// Replaces the buffer content as a user edit would. Raises <see cref="Changed"/> after the buffer is updated, with the
    /// version bumped by one. Throws <see cref="InvalidOperationException"/> when the document is not open, is read-only or the host is crashed.
    /// </summary>
    public void Edit(EditorDocumentId id, string newContent)
    {
        ArgumentNullException.ThrowIfNull(newContent);
        EnsureSurfaceUsable();
        OpenBuffer buffer = FindBuffer(id);
        if (buffer.ReadOnly)
        {
            throw new InvalidOperationException($"Document '{id}' is read-only.");
        }

        buffer.Content = newContent;
        long version = ++_versions[id];
        Changed?.Invoke(this, new EditorChange(id, version, newContent, buffer.IsDirty));
    }

    /// <summary>Asks the client to save. The host does not write anything itself; <see cref="SaveRequested"/> carries the request.</summary>
    public void RequestSave(EditorDocumentId id)
    {
        EnsureSurfaceUsable();
        _ = FindBuffer(id);
        SaveRequested?.Invoke(this, id);
    }

    /// <summary>
    /// Simulates a renderer crash: raises <see cref="Crashed"/> and blocks surface operations until
    /// <see cref="SimulateReady"/>.
    /// </summary>
    public void SimulateCrash(EditorCrashReason reason = EditorCrashReason.RendererExited)
    {
        EnsureNotDisposed();
        _crashed = true;
        Crashed?.Invoke(this, reason);
    }

    /// <summary>Simulates the renderer coming back after a crash and raises <see cref="Ready"/>.</summary>
    public void SimulateReady()
    {
        EnsureNotDisposed();
        _crashed = false;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Delivers a key from the editor surface. The key router goes first: a claimed key returns false and never
    /// reaches <see cref="KeyPressed"/>. An unclaimed key is raised and returns true.
    /// </summary>
    public bool PressKey(EditorKeyEvent key)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureSurfaceUsable();
        if (_handoff is not null && _handoff.TryClaim(key))
        {
            return false;
        }

        KeyPressed?.Invoke(this, key);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _buffers.Clear();
        }

        return ValueTask.CompletedTask;
    }

    private static Task Run(Action action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        try
        {
            action();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private OpenBuffer FindBuffer(EditorDocumentId id) =>
        _buffers.TryGetValue(id, out OpenBuffer? buffer)
            ? buffer
            : throw new InvalidOperationException($"Document '{id}' is not open.");

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsureSurfaceUsable()
    {
        EnsureNotDisposed();
        if (_crashed)
        {
            throw new InvalidOperationException("The editor is crashed. Wait for Ready before using it.");
        }
    }

    private sealed class OpenBuffer(string content, bool readOnly)
    {
        private readonly string _baseline = content;

        public string Content { get; set; } = content;

        public bool ReadOnly { get; set; } = readOnly;

        public bool IsDirty => !string.Equals(Content, _baseline, StringComparison.Ordinal);
    }
}
