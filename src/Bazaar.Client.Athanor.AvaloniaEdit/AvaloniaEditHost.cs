using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;

namespace Bazaar.Client.Athanor.AvaloniaEdit;

/// <summary>A caret position, one-based like the editor status line.</summary>
public readonly record struct EditorCaret(int Line, int Column);

/// <summary>
/// An <see cref="IEditorHost"/> over the AvaloniaEdit <see cref="TextEditor"/> (doc 57 §2, PROPOSED): the default Linux
/// editor, usable on Windows. One control, one buffer per open document; the last opened document is shown. It never touches
/// the network and adds no Ctrl chords: a key the client router claims through <see cref="IEditorKeyHandoff"/> is marked
/// handled before the control acts, every other key is raised on <see cref="KeyPressed"/> and left to the control.
/// Must be used on the UI thread. There is no diff view (<see cref="SupportsDiff"/> is false).
/// </summary>
public sealed class AvaloniaEditHost : IEditorHost
{
    private readonly IEditorKeyHandoff? _handoff;
    private readonly Dictionary<EditorDocumentId, OpenBuffer> _buffers = [];
    private readonly Dictionary<EditorDocumentId, long> _versions = [];
    private readonly TextDocument _emptyDocument = new();
    private OpenBuffer? _active;
    private bool _readyRaised;
    private bool _disposed;

    /// <param name="handoff">The client key router. Keys it claims never reach the editor. Null means no claims.</param>
    public AvaloniaEditHost(IEditorKeyHandoff? handoff = null)
    {
        _handoff = handoff;
        Editor = new TextEditor
        {
            Document = _emptyDocument,
            ShowLineNumbers = true,
            WordWrap = false,
            FontFamily = new FontFamily("Cascadia Code,Consolas,DejaVu Sans Mono,Liberation Mono,monospace"),
        };
        Editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.TextArea.Caret.PositionChanged += OnCaretMoved;
    }

    /// <summary>The control to place in a pane. Owned by this host.</summary>
    public TextEditor Editor { get; }

    /// <summary>Always false: this host has no diff view (doc 57 §2).</summary>
    public static bool SupportsDiff => false;

    /// <summary>The document shown now, or null when none is open.</summary>
    public EditorDocumentId? ActiveDocument => _active?.Id;

    /// <summary>The caret line and column of the shown document, both one-based.</summary>
    public EditorCaret Caret => new(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column);

    /// <summary>The language id of the shown document, or <see cref="LanguageIds.PlainText"/>.</summary>
    public string ActiveLanguageId => _active?.LanguageId ?? LanguageIds.PlainText;

    /// <summary>The colours applied by the last <see cref="SetThemeAsync"/>, or null before a theme was pushed.</summary>
    public EditorColors? Colors { get; private set; }

    public event EventHandler<EditorChange>? Changed;

    public event EventHandler<EditorDocumentId>? SaveRequested;

    public event EventHandler<EditorKeyEvent>? KeyPressed;

    public event EventHandler? Ready;

    /// <summary>Never raised: the control runs in process, so there is no renderer to crash.</summary>
    public event EventHandler<EditorCrashReason>? Crashed
    {
        add { }
        remove { }
    }

    /// <summary>Raised when the caret moves (line and column changed).</summary>
    public event EventHandler<EditorCaret>? CaretMoved;

    public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Run(() =>
        {
            EnsureNotDisposed();
            if (_buffers.Remove(document.Id, out OpenBuffer? previous))
            {
                previous.Detach();
            }

            _versions.TryAdd(document.Id, 0);
            var buffer = new OpenBuffer(this, document);
            _buffers[document.Id] = buffer;
            Activate(buffer);
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
            if (!_buffers.Remove(id, out OpenBuffer? buffer))
            {
                return;
            }

            buffer.Detach();
            if (ReferenceEquals(_active, buffer))
            {
                Activate(_buffers.Values.LastOrDefault());
            }
        }, cancellationToken);

    public Task FocusAsync(CancellationToken cancellationToken) =>
        Run(() =>
        {
            EnsureNotDisposed();
            Editor.Focus();
        }, cancellationToken);

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        return Run(() =>
        {
            EnsureNotDisposed();
            EditorColors colors = EditorColors.Parse(monacoThemeJson);
            if (colors.Background is { } background)
            {
                Editor.Background = new SolidColorBrush(background);
            }

            if (colors.Foreground is { } foreground)
            {
                Editor.Foreground = new SolidColorBrush(foreground);
                Editor.LineNumbersForeground = new SolidColorBrush(foreground, 0.5);
            }

            if (colors.Caret is { } caret)
            {
                Editor.TextArea.CaretBrush = new SolidColorBrush(caret);
            }

            Colors = colors;
        }, cancellationToken);
    }

    public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) =>
        Run(() =>
        {
            EnsureNotDisposed();
            OpenBuffer buffer = Find(id);
            buffer.ReadOnly = isReadOnly;
            if (ReferenceEquals(_active, buffer))
            {
                Editor.IsReadOnly = isReadOnly;
            }
        }, cancellationToken);

    /// <summary>Not supported: this host has no diff view (doc 57 §2). Use the Monaco host or the plain-text fallback for diffs.</summary>
    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("The AvaloniaEdit host has no diff view."));

    /// <summary>Sets the language of an open document. An id with no shipped definition is plain text.</summary>
    public Task SetLanguageAsync(EditorDocumentId id, string languageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(languageId);
        return Run(() =>
        {
            EnsureNotDisposed();
            OpenBuffer buffer = Find(id);
            buffer.LanguageId = languageId;
            if (ReferenceEquals(_active, buffer))
            {
                ApplyHighlighting(languageId);
            }
        }, cancellationToken);
    }

    /// <summary>The current text of an open document.</summary>
    public string GetContent(EditorDocumentId id) => Find(id).Document.Text;

    /// <summary>True when the open document differs from the content it was opened with.</summary>
    public bool IsDirty(EditorDocumentId id) => Find(id).IsDirty;

    /// <summary>Asks the client to save the shown document. The host writes nothing; <see cref="SaveRequested"/> carries the request.</summary>
    public void RequestSave()
    {
        EnsureNotDisposed();
        if (_active is not null)
        {
            SaveRequested?.Invoke(this, _active.Id);
        }
    }

    /// <summary>Moves the caret of the shown document. Values are clamped to the document.</summary>
    public void GoTo(int line, int column)
    {
        EnsureNotDisposed();
        if (_active is null)
        {
            return;
        }

        TextDocument document = _active.Document;
        int safeLine = Math.Clamp(line, 1, document.LineCount);
        DocumentLine documentLine = document.GetLineByNumber(safeLine);
        int safeColumn = Math.Clamp(column, 1, documentLine.Length + 1);
        Editor.TextArea.Caret.Position = new TextViewPosition(safeLine, safeColumn);
        Editor.TextArea.Caret.BringCaretToView();
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Editor.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            Editor.TextArea.Caret.PositionChanged -= OnCaretMoved;
            foreach (OpenBuffer buffer in _buffers.Values)
            {
                buffer.Detach();
            }

            _buffers.Clear();
            _active = null;
            Editor.Document = _emptyDocument;
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

    private void Activate(OpenBuffer? buffer)
    {
        _active = buffer;
        Editor.Document = buffer?.Document ?? _emptyDocument;
        Editor.IsReadOnly = buffer?.ReadOnly ?? false;
        ApplyHighlighting(buffer?.LanguageId ?? LanguageIds.PlainText);
    }

    private void ApplyHighlighting(string languageId)
    {
        string? extension = AvaloniaEditLanguages.ExtensionFor(languageId);
        Editor.SyntaxHighlighting = extension is null ? null : HighlightingManager.Instance.GetDefinitionByExtension(extension);
    }

    private OpenBuffer Find(EditorDocumentId id) =>
        _buffers.TryGetValue(id, out OpenBuffer? buffer)
            ? buffer
            : throw new InvalidOperationException($"Document '{id}' is not open.");

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void OnCaretMoved(object? sender, EventArgs e) => CaretMoved?.Invoke(this, Caret);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var key = new EditorKeyEvent(
            e.Key.ToString(),
            Shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
            Alt: e.KeyModifiers.HasFlag(KeyModifiers.Alt),
            Meta: e.KeyModifiers.HasFlag(KeyModifiers.Meta),
            Ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        if (_handoff is not null && _handoff.TryClaim(key))
        {
            e.Handled = true;
            return;
        }

        KeyPressed?.Invoke(this, key);
    }

    private void OnBufferChanged(OpenBuffer buffer)
    {
        long version = ++_versions[buffer.Id];
        Changed?.Invoke(this, new EditorChange(buffer.Id, version, buffer.Document.Text, buffer.IsDirty));
    }

    private sealed class OpenBuffer
    {
        private readonly AvaloniaEditHost _owner;
        private readonly string _baseline;

        public OpenBuffer(AvaloniaEditHost owner, EditorDocument source)
        {
            _owner = owner;
            _baseline = source.Content;
            Id = source.Id;
            ReadOnly = source.ReadOnly;
            LanguageId = source.LanguageId;
            Document = new TextDocument(source.Content);
            Document.TextChanged += OnTextChanged;
        }

        public EditorDocumentId Id { get; }

        public TextDocument Document { get; }

        public bool ReadOnly { get; set; }

        public string LanguageId { get; set; }

        public bool IsDirty => !string.Equals(Document.Text, _baseline, StringComparison.Ordinal);

        public void Detach() => Document.TextChanged -= OnTextChanged;

        private void OnTextChanged(object? sender, EventArgs e) => _owner.OnBufferChanged(this);
    }
}
