using System.Text;

namespace Bazaar.Client.Athanor.Workspace;

/// <summary>
/// The ordered tab list over one <see cref="IEditorHost"/> (doc 52 §3, §5). Owns the host: disposing the workspace
/// disposes it. Documents come from <see cref="IEditorFileSource"/>; the workspace never touches the network.
/// </summary>
/// <remarks>
/// Each open tab keeps the last content the host reported, so a renderer crash reopens the tab with its unsaved edits
/// rather than the server copy. Operations that await the host or the source are serialised by a gate; host events
/// only take a short state lock and never block on the gate.
/// </remarks>
public sealed class EditorWorkspace : IAsyncDisposable
{
    /// <summary>Documents above this many UTF-8 bytes open read-only (doc 52 §5, default 5 MiB).</summary>
    public const int MaxEditableBytes = 5 * 1024 * 1024;

    private readonly IEditorHost _host;
    private readonly IEditorFileSource _source;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<OpenDocument> _tabs = [];
    private int _activeIndex = -1;
    private bool _disposed;

    /// <summary>Raised when the tab list, the active tab, or a tab's dirty flag changes.</summary>
    public event EventHandler? TabsChanged;

    /// <summary>Raised when the host crashes. The tabs stay open and reopen when the host is ready again.</summary>
    public event EventHandler<EditorCrashReason>? RendererCrashed;

    public EditorWorkspace(IEditorHost host, IEditorFileSource source)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(source);

        _host = host;
        _source = source;
        _host.Changed += OnHostChanged;
        _host.Crashed += OnHostCrashed;
        _host.Ready += OnHostReady;
    }

    /// <summary>The open tabs, in tab order. A snapshot: later changes do not alter the returned list.</summary>
    public IReadOnlyList<EditorTab> Tabs
    {
        get
        {
            lock (_sync)
            {
                return _tabs.Select(t => t.ToTab()).ToArray();
            }
        }
    }

    /// <summary>The active tab, or null when no tab is open.</summary>
    public EditorTab? ActiveTab
    {
        get
        {
            lock (_sync)
            {
                return _activeIndex < 0 ? null : _tabs[_activeIndex].ToTab();
            }
        }
    }

    /// <summary>
    /// Opens a document in a new tab and makes it active. An already-open document is only activated. Returns false
    /// when the source has no such document, or when the content is binary (contains a NUL character).
    /// Content above <see cref="MaxEditableBytes"/> opens read-only.
    /// </summary>
    public async Task<bool> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActivateIfOpen(id))
            {
                return true;
            }

            EditorDocument? loaded = await _source.OpenAsync(id, cancellationToken).ConfigureAwait(false);
            if (loaded is null || loaded.Content.Contains('\0'))
            {
                return false;
            }

            bool readOnly = loaded.ReadOnly || Encoding.UTF8.GetByteCount(loaded.Content) > MaxEditableBytes;
            EditorDocument document = loaded with { ReadOnly = readOnly };
            await _host.OpenAsync(document, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _tabs.Add(new OpenDocument(document));
                _activeIndex = _tabs.Count - 1;
            }

            RaiseTabsChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes a tab. When the closed tab was active, the tab that takes its place (the right neighbour, else the
    /// left) becomes active. Closing a document that is not open does nothing.
    /// </summary>
    public async Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen(id))
            {
                return;
            }

            await _host.CloseAsync(id, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                int index = IndexOf(id);
                _tabs.RemoveAt(index);
                if (_activeIndex == index)
                {
                    _activeIndex = _tabs.Count == 0 ? -1 : Math.Min(index, _tabs.Count - 1);
                }
                else if (_activeIndex > index)
                {
                    _activeIndex--;
                }
            }

            RaiseTabsChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Makes an open tab active. Returns false when the document is not open.</summary>
    public bool Activate(EditorDocumentId id)
    {
        ThrowIfDisposed();
        return ActivateIfOpen(id);
    }

    /// <summary>Moves an open tab to <paramref name="newIndex"/>. The active tab does not change.</summary>
    public void MoveTab(EditorDocumentId id, int newIndex)
    {
        ThrowIfDisposed();
        bool changed;
        lock (_sync)
        {
            int from = IndexOf(id);
            if (from < 0)
            {
                return;
            }

            if (newIndex < 0 || newIndex >= _tabs.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(newIndex), newIndex, "The index is outside the tab list.");
            }

            if (from == newIndex)
            {
                return;
            }

            OpenDocument moved = _tabs[from];
            OpenDocument? active = _activeIndex < 0 ? null : _tabs[_activeIndex];
            _tabs.RemoveAt(from);
            _tabs.Insert(newIndex, moved);
            _activeIndex = active is null ? -1 : _tabs.IndexOf(active);
            changed = true;
        }

        if (changed)
        {
            RaiseTabsChanged();
        }
    }

    /// <summary>Activates the next tab, wrapping from the last to the first. Does nothing when no tab is open.</summary>
    public void NextTab()
    {
        ThrowIfDisposed();
        StepActive(+1);
    }

    /// <summary>Activates the previous tab, wrapping from the first to the last. Does nothing when no tab is open.</summary>
    public void PreviousTab()
    {
        ThrowIfDisposed();
        StepActive(-1);
    }

    /// <summary>Unsubscribes from the host and disposes it. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.Changed -= OnHostChanged;
        _host.Crashed -= OnHostCrashed;
        _host.Ready -= OnHostReady;
        await _host.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private bool ActivateIfOpen(EditorDocumentId id)
    {
        bool changed;
        lock (_sync)
        {
            int index = IndexOf(id);
            if (index < 0)
            {
                return false;
            }

            changed = index != _activeIndex;
            _activeIndex = index;
        }

        if (changed)
        {
            RaiseTabsChanged();
        }

        return true;
    }

    private void StepActive(int step)
    {
        bool changed;
        lock (_sync)
        {
            if (_tabs.Count == 0)
            {
                return;
            }

            int next = _activeIndex < 0
                ? (step > 0 ? 0 : _tabs.Count - 1)
                : ((_activeIndex + step) % _tabs.Count + _tabs.Count) % _tabs.Count;
            changed = next != _activeIndex;
            _activeIndex = next;
        }

        if (changed)
        {
            RaiseTabsChanged();
        }
    }

    // Callers hold _sync or the gate. Equality uses the record struct's value comparison.
    private bool IsOpen(EditorDocumentId id) => IndexOf(id) >= 0;

    private int IndexOf(EditorDocumentId id)
    {
        lock (_sync)
        {
            return _tabs.FindIndex(t => t.Document.Id == id);
        }
    }

    private void OnHostChanged(object? sender, EditorChange change)
    {
        bool dirtyChanged = false;
        lock (_sync)
        {
            int index = IndexOf(change.Id);
            if (index < 0)
            {
                return;
            }

            OpenDocument tab = _tabs[index];
            if (change.Version <= tab.LastVersion)
            {
                return;
            }

            tab.LastVersion = change.Version;
            tab.Document = tab.Document with { Content = change.Content };
            dirtyChanged = tab.Dirty != change.Dirty;
            tab.Dirty = change.Dirty;
        }

        if (dirtyChanged)
        {
            RaiseTabsChanged();
        }
    }

    private void OnHostCrashed(object? sender, EditorCrashReason reason)
    {
        RendererCrashed?.Invoke(this, reason);
    }

    private void OnHostReady(object? sender, EventArgs e)
    {
        // The host is ready again after a crash: reopen every tab with the content we last saw. The handler must not
        // block (the host may raise Ready from inside OpenAsync), so the work runs on its own task.
        _ = ReopenAllAsync();
    }

    private async Task ReopenAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<EditorDocument> snapshot;
            lock (_sync)
            {
                snapshot = _tabs.Select(t => t.Document).ToList();
                foreach (OpenDocument tab in _tabs)
                {
                    tab.LastVersion = -1;
                }
            }

            foreach (EditorDocument document in snapshot)
            {
                try
                {
                    await _host.OpenAsync(document, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The tab stays in the list. The next Ready reopens it, so a single failed reopen does not fault
                    // the whole workspace.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RaiseTabsChanged() => TabsChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class OpenDocument(EditorDocument document)
    {
        public EditorDocument Document { get; set; } = document;

        public bool Dirty { get; set; }

        public long LastVersion { get; set; } = -1;

        public EditorTab ToTab() => new(
            Document.Id,
            TitleOf(Document.Id.Path),
            Dirty,
            Document.ReadOnly,
            Document.LanguageId);

        private static string TitleOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }
    }
}
