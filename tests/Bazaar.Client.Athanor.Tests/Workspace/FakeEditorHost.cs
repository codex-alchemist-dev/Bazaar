namespace Bazaar.Client.Athanor.Tests.Workspace;

/// <summary>In-memory <see cref="IEditorHost"/> that records calls and lets a test raise host events.</summary>
internal sealed class FakeEditorHost : IEditorHost
{
    private EventHandler<EditorChange>? _changed;
    private EventHandler<EditorDocumentId>? _saveRequested;
    private EventHandler<EditorKeyEvent>? _keyPressed;
    private EventHandler? _ready;
    private EventHandler<EditorCrashReason>? _crashed;

    public List<EditorDocument> Opened { get; } = [];

    public List<EditorDocumentId> Closed { get; } = [];

    /// <summary>When set, every OpenAsync call throws, to simulate a host that cannot show a document.</summary>
    public bool FailOpens { get; set; }

    public int OpenAttempts { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>How many handlers are subscribed to <see cref="Changed"/>.</summary>
    public int ChangedSubscribers => _changed?.GetInvocationList().Length ?? 0;

    public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        OpenAttempts++;
        if (FailOpens)
        {
            throw new InvalidOperationException("The fake host refused to open the document.");
        }

        Opened.Add(document);
        return Task.CompletedTask;
    }

    public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        Closed.Add(id);
        return Task.CompletedTask;
    }

    public Task FocusAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public List<string> Themes { get; } = [];

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken)
    {
        Themes.Add(monacoThemeJson);
        return Task.CompletedTask;
    }

    public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken) => Task.CompletedTask;

    public event EventHandler<EditorChange>? Changed
    {
        add => _changed += value;
        remove => _changed -= value;
    }

    public event EventHandler<EditorDocumentId>? SaveRequested
    {
        add => _saveRequested += value;
        remove => _saveRequested -= value;
    }

    public event EventHandler<EditorKeyEvent>? KeyPressed
    {
        add => _keyPressed += value;
        remove => _keyPressed -= value;
    }

    public event EventHandler? Ready
    {
        add => _ready += value;
        remove => _ready -= value;
    }

    public event EventHandler<EditorCrashReason>? Crashed
    {
        add => _crashed += value;
        remove => _crashed -= value;
    }

    public void RaiseChanged(EditorChange change) => _changed?.Invoke(this, change);

    public void RaiseReady() => _ready?.Invoke(this, EventArgs.Empty);

    public void RaiseCrashed(EditorCrashReason reason) => _crashed?.Invoke(this, reason);

    public void RaiseSaveRequested(EditorDocumentId id) => _saveRequested?.Invoke(this, id);

    public void RaiseKeyPressed(EditorKeyEvent key) => _keyPressed?.Invoke(this, key);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
