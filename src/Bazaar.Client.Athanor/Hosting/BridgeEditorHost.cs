using Bazaar.Client.Athanor.Bridge;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// An <see cref="IEditorHost"/> that speaks the doc 57 §4 bridge over an <see cref="IEditorBridgeTransport"/>. It owns the
/// bridge rules and nothing about the window: the codec, the host and editor sequence numbers, the key claim table, and
/// the crash and reload handling. The Windows host supplies a WebView2 transport; tests supply a fake.
/// </summary>
/// <remarks>
/// Messages sent before the page reports <c>ready</c> wait in order and are flushed when it does. When the page reports
/// <c>ready</c> a second time (a reload after a crash) the sequence numbers restart, and the theme, the claim table and
/// the open documents are sent again with their latest content, so nothing the user typed is lost (doc 57 §5).
/// </remarks>
public sealed class BridgeEditorHost : IEditorHost
{
    private readonly IEditorBridgeTransport _transport;
    private readonly IEditorKeyHandoff? _handoff;
    private readonly IClaimedKeySource? _claims;
    private readonly BridgeCodec _codec;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<Func<long, HostMessage>> _pending = [];
    private BridgeSequenceGuard _guard = new();
    private long _outSeq;
    private string? _themeJson;
    private bool _ready;
    private bool _everReady;
    private bool _crashed;
    private bool _disposed;

    public BridgeEditorHost(
        IEditorBridgeTransport transport,
        IEditorKeyHandoff? handoff = null,
        IClaimedKeySource? claims = null,
        BridgeCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _handoff = handoff;
        _claims = claims;
        _codec = codec ?? new BridgeCodec();
        _transport.MessageReceived += OnMessage;
        _transport.Faulted += OnFaulted;
        if (_claims is not null)
        {
            _claims.Changed += OnClaimsChanged;
        }
    }

    public event EventHandler<EditorChange>? Changed;

    public event EventHandler<EditorDocumentId>? SaveRequested;

    public event EventHandler<EditorKeyEvent>? KeyPressed;

    /// <summary>
    /// Raised after the client key router claimed a key the page swallowed. The router has already acted on it; a window host
    /// uses this to take native keyboard focus out of the editor surface (doc 57 §3a).
    /// </summary>
    public event EventHandler<EditorKeyEvent>? KeyClaimed;

    public event EventHandler? Ready;

    public event EventHandler<EditorCrashReason>? Crashed;

    /// <summary>Raised when the editor page reports an <c>error</c> message. It is not fatal; the host stays up.</summary>
    public event EventHandler<ErrorMessage>? ErrorReported;

    /// <summary>The number of inbound texts the codec or the sequence guard refused since the host was created.</summary>
    public int RejectedInboundCount { get; private set; }

    /// <summary>True between a crash and the next <c>ready</c> from the reloaded page.</summary>
    public bool IsCrashed => _crashed;

    public async Task OpenAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ThrowIfUnusable();
        string key = document.Id.ToString();
        _documents[key] = new OpenDocument(document, document.Content);
        await SendAsync(seq => new OpenMessage(seq, key, document.Content, document.Revision, document.LanguageId, document.ReadOnly), cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        string key = id.ToString();
        _documents.Remove(key);
        await SendAsync(seq => new CloseMessage(seq, key), cancellationToken).ConfigureAwait(false);
    }

    public Task FocusAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        return SendAsync(seq => new FocusMessage(seq), cancellationToken);
    }

    public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monacoThemeJson);
        ThrowIfUnusable();
        _themeJson = monacoThemeJson;
        return SendAsync(seq => new SetThemeMessage(seq, monacoThemeJson), cancellationToken);
    }

    public async Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken)
    {
        ThrowIfUnusable();
        string key = id.ToString();
        if (_documents.TryGetValue(key, out OpenDocument? open))
        {
            _documents[key] = open with { Document = open.Document with { ReadOnly = isReadOnly } };
        }

        await SendAsync(seq => new SetReadOnlyMessage(seq, key, isReadOnly), cancellationToken).ConfigureAwait(false);
    }

    public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(languageId);
        ThrowIfUnusable();
        return SendAsync(seq => new ShowDiffMessage(seq, original, modified, languageId), cancellationToken);
    }

    /// <summary>
    /// Asks the client to save an open document, as the AvaloniaEdit host's <c>RequestSave</c> does (doc 57 §8). Raises
    /// <see cref="SaveRequested"/>; the host writes nothing. False when the document is not open here.
    /// </summary>
    public bool RequestSave(EditorDocumentId id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_documents.ContainsKey(id.ToString()))
        {
            return false;
        }

        SaveRequested?.Invoke(this, id);
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _transport.MessageReceived -= OnMessage;
        _transport.Faulted -= OnFaulted;
        if (_claims is not null)
        {
            _claims.Changed -= OnClaimsChanged;
        }

        return _transport.DisposeAsync();
    }

    /// <summary>The chords to send: the source's table minus anything unsafe (a Ctrl chord or a blank entry).</summary>
    public static string[] SafeChords(IEnumerable<string> chords) =>
        [.. chords
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Where(c => !c.Split('+').Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)];

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_crashed)
        {
            throw new InvalidOperationException("The editor surface crashed and has not been reloaded.");
        }
    }

    private async Task SendAsync(Func<long, HostMessage> build, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_ready)
            {
                _pending.Add(build);
                return;
            }

            await PostAsync(build, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task PostAsync(Func<long, HostMessage> build, CancellationToken cancellationToken)
    {
        string json = _codec.Encode(build(++_outSeq));
        await _transport.PostAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private void OnClaimsChanged(object? sender, EventArgs e) => _ = PushClaimsAsync();

    private async Task PushClaimsAsync()
    {
        if (_disposed || _crashed)
        {
            return;
        }

        try
        {
            await SendAsync(BuildClaimsMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The host went away while the table was changing; the next ready resends it.
        }
    }

    private SetClaimedKeysMessage BuildClaimsMessage(long seq) => new(seq, SafeChords(_claims?.Chords ?? []));

    private void OnFaulted(object? sender, EditorCrashReason reason)
    {
        if (_disposed)
        {
            return;
        }

        _crashed = true;
        _ready = false;
        Crashed?.Invoke(this, reason);
    }

    private void OnMessage(object? sender, string json)
    {
        if (_disposed)
        {
            return;
        }

        // A reloaded page starts its own sequence at 1, so a guard that already saw higher numbers would refuse it.
        // The ready message is the one message that may restart the sequence, and only while the host is not ready.
        if (!_ready && _codec.TryDecode(json, out EditorMessage? first, out _) && first is ReadyMessage ready)
        {
            _guard = new BridgeSequenceGuard();
            _guard.TryAccept(ready.Seq);
            _ = BeginReadyAsync();
            return;
        }

        if (!_codec.TryDecode(json, _guard, out EditorMessage? message, out _))
        {
            RejectedInboundCount++;
            return;
        }

        switch (message)
        {
            case ChangedMessage changed when _documents.TryGetValue(changed.DocumentId, out OpenDocument? open):
                _documents[changed.DocumentId] = open with { LatestContent = changed.Content };
                Changed?.Invoke(this, new EditorChange(open.Document.Id, changed.Version, changed.Content, changed.Dirty));
                break;

            case SaveRequestedMessage save when _documents.TryGetValue(save.DocumentId, out OpenDocument? target):
                SaveRequested?.Invoke(this, target.Document.Id);
                break;

            case KeyPressedMessage key:
                HandleKey(key);
                break;

            case ErrorMessage error:
                ErrorReported?.Invoke(this, error);
                break;

            case ChangedMessage or SaveRequestedMessage:
                RejectedInboundCount++; // a document id the host never opened
                break;

            default:
                break; // selection and a duplicate ready carry nothing the host acts on
        }
    }

    private void HandleKey(KeyPressedMessage message)
    {
        if (message.Ctrl)
        {
            return; // the client has no Ctrl chords; a page that reports one is out of step with the table
        }

        var key = new EditorKeyEvent(message.Key, message.Shift, message.Alt, message.Meta, message.Ctrl);
        if (_handoff is not null && _handoff.TryClaim(key))
        {
            KeyClaimed?.Invoke(this, key); // the router acted on it; the editor never sees it
            return;
        }

        KeyPressed?.Invoke(this, key);
    }

    private async Task BeginReadyAsync()
    {
        try
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            bool reload = _everReady;
            _crashed = false;
            _outSeq = 0;
            var queued = new List<Func<long, HostMessage>>(_pending);
            _pending.Clear();
            _ready = true;
            _everReady = true;
            if (reload && _themeJson is not null)
            {
                string theme = _themeJson;
                await PostAsync(seq => new SetThemeMessage(seq, theme), CancellationToken.None).ConfigureAwait(false);
            }

            if (_claims is not null)
            {
                await PostAsync(BuildClaimsMessage, CancellationToken.None).ConfigureAwait(false);
            }

            if (reload)
            {
                foreach (OpenDocument open in _documents.Values.ToArray())
                {
                    EditorDocument d = open.Document;
                    await PostAsync(seq => new OpenMessage(seq, d.Id.ToString(), open.LatestContent, d.Revision, d.LanguageId, d.ReadOnly), CancellationToken.None).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (Func<long, HostMessage> build in queued)
                {
                    await PostAsync(build, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
            OnFaulted(this, EditorCrashReason.BridgeFault);
            return;
        }
        finally
        {
            _sendGate.Release();
        }

        Ready?.Invoke(this, EventArgs.Empty);
    }

    private sealed record OpenDocument(EditorDocument Document, string LatestContent);
}
