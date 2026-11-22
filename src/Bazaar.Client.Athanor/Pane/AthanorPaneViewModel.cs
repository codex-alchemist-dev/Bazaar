using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Saves;
using Bazaar.Client.Athanor.Workspace;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Athanor.Pane;

/// <summary>One tab as the pane shows it: the workspace tab plus the dirty flag the save coordinator holds.</summary>
public sealed record AthanorTabItem(EditorDocumentId Id, string Title, bool Dirty, bool ReadOnly, bool Active)
{
    /// <summary>The label the tab strip shows. A dirty tab carries a trailing dot.</summary>
    public string Label => Dirty ? Title + " ●" : Title;
}

/// <summary>
/// The Athanor editor pane (doc 57 §2): the tab list, the active tab, dirty flags and the save and conflict state, over an
/// <see cref="EditorWorkspace"/> and a <see cref="SaveCoordinator"/>. Built without a host or a file source it is an empty
/// state, so a head that has no editor wired still renders the pane.
/// </summary>
public sealed class AthanorPaneViewModel : IPaneViewModel, IAsyncDisposable
{
    private readonly EditorWorkspace? _workspace;
    private readonly SaveCoordinator? _saves;
    private readonly IEditorHost? _host;
    private readonly RecordingSource? _source;
    private readonly IEditorDraftStore? _drafts;
    private readonly IEditorFileSource? _rawSource;
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<EditorDocumentId, (EditorDocument Document, EditorDraft Draft)> _pending = [];

    /// <summary>Builds the empty state. Nothing can be opened.</summary>
    public AthanorPaneViewModel(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        Title = title;
    }

    /// <summary>Builds the working pane over a host and a file source.</summary>
    public AthanorPaneViewModel(string title, IEditorHost host, IEditorFileSource source, IEditorDraftStore drafts, TimeProvider? clock = null)
        : this(title)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(drafts);

        _host = host;
        _drafts = drafts;
        _rawSource = source;
        _source = new RecordingSource(source);
        _workspace = new EditorWorkspace(host, _source);
        _saves = new SaveCoordinator(_source, drafts, clock ?? TimeProvider.System);

        _host.Changed += OnHostChanged;
        _host.SaveRequested += OnHostSaveRequested;
        _workspace.TabsChanged += OnStateChanged;
        _saves.StateChanged += OnSaveStateChanged;
    }

    public string Kind => AthanorPaneKinds.Editor;

    public string Title { get; }

    /// <summary>True when no host or file source was supplied. The view shows <see cref="EmptyMessage"/>.</summary>
    public bool IsEmpty => _workspace is null;

    public const string EmptyMessage = "The editor is not available in this client.";

    /// <summary>Raised after the tab list, the active tab or any save state changed. May run on any thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AthanorTabItem> Tabs
    {
        get
        {
            if (_workspace is null)
            {
                return [];
            }

            var active = _workspace.ActiveTab?.Id;
            return _workspace.Tabs
                .Select(t => new AthanorTabItem(t.Id, t.Title, IsDirty(t.Id), t.ReadOnly, active == t.Id))
                .ToArray();
        }
    }

    public AthanorTabItem? ActiveTab => Tabs.FirstOrDefault(t => t.Active);

    /// <summary>The content of the active document as the coordinator last saw it, or empty when no tab is open.</summary>
    public string ActiveContent => ActiveState?.Content ?? string.Empty;

    /// <summary>The conflict the last save hit on the active document, or null. The server's copy is in the conflict.</summary>
    public SaveConflict? ActiveConflict => ActiveState?.Conflict;

    /// <summary>Why the last save did not reach the server (offline, source error), or null.</summary>
    public string? ActiveUnavailableReason => ActiveState?.UnavailableReason;

    /// <summary>
    /// A local draft found for the active document when it was opened, or null. The view offers it back; it stays pending
    /// until <see cref="RestoreDraftAsync"/> or <see cref="DiscardDraftAsync"/>. Read-only documents are never offered one.
    /// </summary>
    public EditorDraft? PendingDraft
    {
        get
        {
            if (_workspace?.ActiveTab is not { } tab)
            {
                return null;
            }

            lock (_pendingGate)
            {
                return _pending.TryGetValue(tab.Id, out var entry) ? entry.Draft : null;
            }
        }
    }

    /// <summary>Opens a document in a new tab, or activates it when open. False when it does not exist or cannot be shown.</summary>
    public async Task<bool> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken = default)
    {
        if (_workspace is null || _saves is null || _source is null)
        {
            return false;
        }

        if (!await _workspace.OpenAsync(id, cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        if (_saves.GetState(id) is null && _source.TryTake(id, out var document))
        {
            _saves.Track(document);
            await OfferDraftAsync(document, cancellationToken).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Applies the pending draft to the active document: it becomes dirty with the draft's content and the draft's base
    /// revision, so saving over a newer server revision conflicts instead of overwriting. False when nothing is pending.
    /// </summary>
    public async Task<bool> RestoreDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_saves is null || _host is null || TakePending() is not var (document, draft))
        {
            return false;
        }

        _saves.Track(document, draft);
        // Show the draft text in the editor surface; the coordinator already holds it as dirty content.
        await _host.OpenAsync(document with { Content = draft.Content }, cancellationToken).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Deletes the pending draft of the active document and keeps the server's copy. False when nothing is pending.</summary>
    public async Task<bool> DiscardDraftAsync(CancellationToken cancellationToken = default)
    {
        if (_drafts is null || TakePending() is not var (_, draft))
        {
            return false;
        }

        await _drafts.DeleteAsync(draft.Id, cancellationToken).ConfigureAwait(true);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Shows the active document's current text against the server copy in the host's diff component. False when no
    /// document is open or the server no longer has the document.
    /// </summary>
    public async Task<bool> ShowActiveDiffAsync(CancellationToken cancellationToken = default)
    {
        if (_workspace?.ActiveTab is not { } tab || _host is null || _rawSource is null)
        {
            return false;
        }

        var server = await _rawSource.OpenAsync(tab.Id, cancellationToken).ConfigureAwait(true);
        if (server is null)
        {
            return false;
        }

        var current = ActiveState?.Content ?? server.Content;
        await Bazaar.Client.Athanor.Sources.DiffSession.ShowAsync(_host, server.Content, current, tab.Id.Path, LanguageRegistry.Default, cancellationToken).ConfigureAwait(true);
        return true;
    }

    public bool Activate(EditorDocumentId id) => _workspace?.Activate(id) ?? false;

    public async Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken = default)
    {
        if (_workspace is null || _saves is null)
        {
            return;
        }

        await _workspace.CloseAsync(id, cancellationToken).ConfigureAwait(true);
        _saves.Untrack(id);
        lock (_pendingGate)
        {
            _pending.Remove(id);
        }
    }

    /// <summary>Saves the active document. Null when no document is open. A conflict stays in <see cref="ActiveConflict"/>.</summary>
    public Task<SaveOutcome?> SaveActiveAsync(CancellationToken cancellationToken = default) =>
        _workspace?.ActiveTab is { } tab ? SaveAsync(tab.Id, cancellationToken) : Task.FromResult<SaveOutcome?>(null);

    /// <summary>Keeps the user's text over the server's copy after a conflict. The next save goes against the server revision.</summary>
    public Task KeepMineAsync() =>
        _workspace?.ActiveTab is { } tab && _saves is not null ? _saves.ResolveConflictKeepMineAsync(tab.Id) : Task.CompletedTask;

    /// <summary>
    /// Types into the active document. Only the plain-text fallback host takes input from the pane; a Monaco host owns its
    /// own surface and reports through <see cref="IEditorHost.Changed"/>.
    /// </summary>
    public void EditActive(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_host is PlainTextFallbackHost fallback && _workspace?.ActiveTab is { ReadOnly: false } tab)
        {
            fallback.Edit(tab.Id, content);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_workspace is null || _saves is null || _host is null)
        {
            return;
        }

        _host.Changed -= OnHostChanged;
        _host.SaveRequested -= OnHostSaveRequested;
        _workspace.TabsChanged -= OnStateChanged;
        _saves.StateChanged -= OnSaveStateChanged;
        _saves.Dispose();
        await _workspace.DisposeAsync().ConfigureAwait(false);
    }

    private async Task OfferDraftAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        if (_drafts is null || document.ReadOnly)
        {
            return;
        }

        var draft = await _drafts.ReadAsync(document.Id, cancellationToken).ConfigureAwait(true);
        if (draft is null || (draft.BaseRevision == document.Revision && draft.Content == document.Content))
        {
            return;
        }

        lock (_pendingGate)
        {
            _pending[document.Id] = (document, draft);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private (EditorDocument Document, EditorDraft Draft)? TakePending()
    {
        if (_workspace?.ActiveTab is not { } tab)
        {
            return null;
        }

        lock (_pendingGate)
        {
            return _pending.Remove(tab.Id, out var entry) ? entry : null;
        }
    }

    private DocumentSaveState? ActiveState =>
        _workspace?.ActiveTab is { } tab ? _saves?.GetState(tab.Id) : null;

    private bool IsDirty(EditorDocumentId id) => _saves?.GetState(id)?.Dirty ?? false;

    private async Task<SaveOutcome?> SaveAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        if (_saves?.GetState(id) is null)
        {
            return null;
        }

        return await _saves.SaveAsync(id, cancellationToken).ConfigureAwait(true);
    }

    private void OnHostChanged(object? sender, EditorChange change) => _saves?.OnChanged(change);

    private void OnHostSaveRequested(object? sender, EditorDocumentId id) => _ = SaveAsync(id, CancellationToken.None);

    private void OnStateChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnSaveStateChanged(object? sender, EditorDocumentId id) => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Passes calls through and remembers the last document each open returned, so the pane can track its revision.</summary>
    private sealed class RecordingSource(IEditorFileSource inner) : IEditorFileSource
    {
        private readonly Dictionary<EditorDocumentId, EditorDocument> _opened = [];

        public async Task<EditorDocument?> OpenAsync(EditorDocumentId id, CancellationToken cancellationToken)
        {
            var document = await inner.OpenAsync(id, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                lock (_opened)
                {
                    _opened[id] = document;
                }
            }

            return document;
        }

        public Task<SaveOutcome> SaveAsync(EditorDocumentId id, string content, string baseRevision, CancellationToken cancellationToken) =>
            inner.SaveAsync(id, content, baseRevision, cancellationToken);

        public bool TryTake(EditorDocumentId id, out EditorDocument document)
        {
            lock (_opened)
            {
                return _opened.Remove(id, out document!);
            }
        }
    }
}
