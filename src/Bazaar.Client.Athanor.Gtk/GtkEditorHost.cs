using System.Text;
using System.Text.Json;
using Bazaar.Client.Athanor.Bridge;
using Bazaar.Client.Athanor.Gtk.Protocol;

namespace Bazaar.Client.Athanor.Gtk;

/// <summary>
/// An <see cref="IEditorHost"/> that runs Monaco in a separate GTK helper process, in the helper's own top-level
/// window (doc 57 section 2.1). The proxy speaks the doc 57 section 4 bridge with the existing <see cref="BridgeCodec"/>
/// and <see cref="BridgeSequenceGuard"/>, supervises the helper, reports <see cref="EditorCrashReason"/> when it dies,
/// restarts it with backoff, and keeps every open document's latest content so typing survives a crash.
/// </summary>
public sealed class GtkEditorHost : IEditorHost
{
    private readonly IEditorHelperLauncher _launcher;
    private readonly GtkEditorHostOptions _options;
    private readonly IEditorDraftStore? _drafts;
    private readonly TimeProvider _time;
    private readonly BridgeCodec _codec;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DocumentState> _documents = new(StringComparer.Ordinal);
    private readonly List<string> _openOrder = [];

    private string? _themeJson;
    private EditorWindowPlacement? _window;
    private Session? _session;
    private Task? _supervisor;
    private TaskCompletionSource _firstReady = NewSignal();
    private int _failures;
    private int _totalRestarts;
    private int _disposed;
    private EditorHelperState _state = EditorHelperState.Stopped;
    private string? _lastFailure;

    /// <summary>Creates the proxy. Nothing starts until <see cref="StartAsync"/>.</summary>
    /// <param name="launcher">Starts helpers. Use <see cref="ProcessEditorHelperLauncher"/> in production.</param>
    /// <param name="options">Timeouts and limits; defaults when null.</param>
    /// <param name="drafts">When set, dirty documents are written here when the helper dies (doc 57 section 5).</param>
    /// <param name="timeProvider">Clock for timeouts and backoff; the system clock when null.</param>
    public GtkEditorHost(
        IEditorHelperLauncher launcher,
        GtkEditorHostOptions? options = null,
        IEditorDraftStore? drafts = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        _launcher = launcher;
        _options = options ?? new GtkEditorHostOptions();
        _options.Validate();
        _drafts = drafts;
        _time = timeProvider ?? TimeProvider.System;
        _codec = new BridgeCodec(_options.MaxPayloadBytes);
    }

    public event EventHandler<EditorChange>? Changed;

    public event EventHandler<EditorDocumentId>? SaveRequested;

    public event EventHandler<EditorKeyEvent>? KeyPressed;

    public event EventHandler? Ready;

    public event EventHandler<EditorCrashReason>? Crashed;

    /// <summary>The editor reported an error (a bridge <c>error</c> message). The helper keeps running.</summary>
    public event EventHandler<ErrorMessage>? ErrorReported;

    /// <summary>The user closed the helper's window. The helper is still running and the window is hidden.</summary>
    public event EventHandler? WindowClosed;

    /// <summary>The state changed.</summary>
    public event EventHandler<EditorHelperState>? StateChanged;

    /// <summary>The current state.</summary>
    public EditorHelperState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>Why the last helper failed, for logs and the status page. Null when none has.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_stateLock)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>How many times the helper was restarted since this host was created.</summary>
    public int TotalRestarts => Volatile.Read(ref _totalRestarts);

    /// <summary>
    /// Starts the helper and completes when the page is ready. If the first start fails the proxy keeps retrying with
    /// backoff; this completes when one succeeds, and fails with <see cref="EditorHelperException"/> when it gives up.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_stateLock)
        {
            _supervisor ??= Task.Run(SuperviseAsync, CancellationToken.None);
        }

        return _firstReady.Task.WaitAsync(cancellationToken);
    }

    /// <summary>After <see cref="EditorHelperState.Faulted"/>: clears the failure count and starts again.</summary>
    public Task RestartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        lock (_stateLock)
        {
            if (_state == EditorHelperState.Faulted)
            {
                _failures = 0;
                _firstReady = NewSignal();
                _supervisor = Task.Run(SuperviseAsync, CancellationToken.None);
            }
        }

        return _firstReady.Task.WaitAsync(cancellationToken);
    }

    public async Task OpenAsync(EditorDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        string key = document.Id.ToString();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? ready = ReadySession();
            if (ready is not null)
            {
                // Encode first: an oversize document is refused before any state changes.
                await SendHostAsync(ready, seq => ToOpen(seq, key, document.Content, document), cancellationToken).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _documents[key] = new DocumentState(document, document.Content, document.ReadOnly);
                _openOrder.Remove(key);
                _openOrder.Add(key);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken)
    {
        string key = id.ToString();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _documents.Remove(key);
                _openOrder.Remove(key);
            }

            Session? ready = ReadySession();
            if (ready is not null)
            {
                await SendHostAsync(ready, seq => new CloseMessage(seq, key), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task FocusAsync(CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? ready = ReadySession();
            if (ready is not null)
            {
                await SendHostAsync(ready, seq => new FocusMessage(seq), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monacoThemeJson);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session? ready = ReadySession();
            if (ready is not null)
            {
                await SendHostAsync(ready, seq => new SetThemeMessage(seq, monacoThemeJson), cancellationToken).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _themeJson = monacoThemeJson;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken)
    {
        string key = id.ToString();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_documents.TryGetValue(key, out DocumentState? state))
                {
                    state.ReadOnly = isReadOnly;
                }
            }

            Session? ready = ReadySession();
            if (ready is not null)
            {
                await SendHostAsync(ready, seq => new SetReadOnlyMessage(seq, key, isReadOnly), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Shows the diff. Needs a running helper; a diff is a view, not state, so it is not replayed after a restart.</summary>
    public async Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(modified);
        ArgumentNullException.ThrowIfNull(languageId);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Session ready = ReadySession() ?? throw new EditorHelperException("The editor helper is not running.");
            await SendHostAsync(ready, seq => new ShowDiffMessage(seq, original, modified, languageId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Places and shows or hides the helper's window. Size, title and visibility are always honoured; position only where
    /// the display server lets an application move its window (X11).
    /// </summary>
    public async Task SetWindowAsync(EditorWindowPlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _window = placement;
            }

            Session? ready = ReadySession();
            if (ready is not null)
            {
                await WriteAsync(ready, WindowLine(placement), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Session? session;
        Task? supervisor;
        lock (_stateLock)
        {
            session = _session;
            supervisor = _supervisor;
        }

        if (session is { IsReady: true })
        {
            try
            {
                using var shutdownTimeout = new CancellationTokenSource(_options.ShutdownGrace);
                await _sendLock.WaitAsync(shutdownTimeout.Token).ConfigureAwait(false);
                try
                {
                    await session.Connection.WriteLineAsync(HelperProtocol.BuildShutdown(), shutdownTimeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                await session.Ended.Task.WaitAsync(_options.ShutdownGrace).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // A helper that does not leave on request is killed below.
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(_options.ExitGrace + _options.ShutdownGrace).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The supervisor ends on its own once the helper is killed.
            }
        }

        _firstReady.TrySetCanceled();
        SetState(EditorHelperState.Disposed);
        _lifetime.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string WindowLine(EditorWindowPlacement p) =>
        HelperProtocol.BuildWindow(p.X, p.Y, p.Width, p.Height, p.Visible, p.Title);

    private static OpenMessage ToOpen(long seq, string key, string content, EditorDocument document) =>
        new(seq, key, content, document.Revision, document.LanguageId, document.ReadOnly);

    private Session? ReadySession()
    {
        lock (_stateLock)
        {
            return _session is { IsReady: true } ? _session : null;
        }
    }

    private void SetState(EditorHelperState state)
    {
        lock (_stateLock)
        {
            if (_state == state || _state == EditorHelperState.Disposed)
            {
                return;
            }

            _state = state;
        }

        Raise(StateChanged, state);
    }

    // The supervisor: run a helper, and when it dies report it, write drafts, back off and run the next one.
    private async Task SuperviseAsync()
    {
        CancellationToken lifetime = _lifetime.Token;
        while (!lifetime.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            SetState(EditorHelperState.Starting);
            SessionOutcome outcome = await RunSessionAsync(lifetime).ConfigureAwait(false);
            if (lifetime.IsCancellationRequested || Volatile.Read(ref _disposed) == 1)
            {
                break;
            }

            await PersistDraftsAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _lastFailure = outcome.Detail;
            }

            Raise(Crashed, outcome.Reason);

            if (outcome.WasReady && outcome.Lifetime >= _options.StableAfter)
            {
                _failures = 0;
            }

            _failures++;
            if (_failures > _options.MaxRestarts)
            {
                SetState(EditorHelperState.Faulted);
                _firstReady.TrySetException(new EditorHelperException(
                    $"The editor helper failed {_failures} times in a row and was given up on. Last failure: {outcome.Detail}"));
                return;
            }

            SetState(EditorHelperState.Backoff);
            try
            {
                await Task.Delay(Backoff(_failures), _time, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Interlocked.Increment(ref _totalRestarts);
        }
    }

    private TimeSpan Backoff(int failures)
    {
        double factor = Math.Pow(2, Math.Min(failures - 1, 30));
        double milliseconds = Math.Min(_options.BackoffMax.TotalMilliseconds, _options.BackoffInitial.TotalMilliseconds * factor);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private async Task<SessionOutcome> RunSessionAsync(CancellationToken lifetime)
    {
        long startedAt = _time.GetTimestamp();
        IEditorHelperConnection connection;
        try
        {
            connection = await _launcher.LaunchAsync(lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return new SessionOutcome(EditorCrashReason.Unknown, false, TimeSpan.Zero, "Cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionOutcome(EditorCrashReason.Unknown, false, TimeSpan.Zero, "The helper could not be started: " + ex.Message);
        }

        var session = new Session(connection, lifetime);
        lock (_stateLock)
        {
            _session = session;
        }

        Task watchdog = WatchdogAsync(session);
        try
        {
            await ReadLoopAsync(session).ConfigureAwait(false);
        }
        finally
        {
            await session.Cancel().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The watchdog stopped with the session.
            }

            lock (_stateLock)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
            }
        }

        int? exitCode = await WaitForExitAsync(connection).ConfigureAwait(false);
        string detail = session.FaultDetail ?? "The helper process ended.";
        if (exitCode is { } code)
        {
            detail += $" Exit code {code}.";
        }

        string? tail = connection.DiagnosticTail;
        if (!string.IsNullOrEmpty(tail))
        {
            detail += " Helper output: " + tail;
        }

        EditorCrashReason reason = session.FaultReason ?? EditorCrashReason.RendererExited;
        TimeSpan lived = _time.GetElapsedTime(startedAt);
        session.Ended.TrySetResult();
        await connection.DisposeAsync().ConfigureAwait(false);
        return new SessionOutcome(reason, session.WasReady, lived, detail);
    }

    private async Task<int?> WaitForExitAsync(IEditorHelperConnection connection)
    {
        try
        {
            return await connection.Exited.WaitAsync(_options.ExitGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            connection.Kill();
            try
            {
                return await connection.Exited.WaitAsync(_options.ExitGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    // Reads lines until the helper's output ends, a protocol error, or cancellation.
    private async Task ReadLoopAsync(Session session)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await session.Connection.ReadLineAsync(session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidDataException ex)
            {
                session.Fault(EditorCrashReason.BridgeFault, ex.Message);
                return;
            }
            catch (IOException ex)
            {
                session.Fault(EditorCrashReason.RendererExited, "The helper's output broke: " + ex.Message);
                return;
            }

            if (line is null)
            {
                return;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (!await HandleLineAsync(session, line).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> HandleLineAsync(Session session, string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > _codec.MaxPayloadBytes)
        {
            session.Fault(EditorCrashReason.BridgeFault, "A line from the helper is over the payload limit.");
            return false;
        }

        if (!HelperProtocol.TryGetType(line, out string type))
        {
            session.Fault(EditorCrashReason.BridgeFault, "The helper sent a line that is not a JSON object with a type.");
            return false;
        }

        if (HelperProtocol.IsControl(type))
        {
            return HandleControl(session, type, line);
        }

        if (!session.HelloSeen)
        {
            session.Fault(EditorCrashReason.BridgeFault, "The helper's first line was not helper.hello.");
            return false;
        }

        if (!_codec.TryDecode(line, session.Guard, out EditorMessage? message, out string? error))
        {
            session.Fault(EditorCrashReason.BridgeFault, "Bridge message refused: " + error);
            return false;
        }

        if (message is ReadyMessage)
        {
            return await OnReadyAsync(session).ConfigureAwait(false);
        }

        if (!session.IsReady)
        {
            session.Fault(EditorCrashReason.BridgeFault, "The editor sent a message before ready.");
            return false;
        }

        Dispatch(message!);
        return true;
    }

    private bool HandleControl(Session session, string type, string line)
    {
        if (type == HelperProtocol.Hello)
        {
            if (session.HelloSeen)
            {
                session.Fault(EditorCrashReason.BridgeFault, "The helper sent helper.hello twice.");
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            bool versionOk = root.TryGetProperty("protocol", out JsonElement protocol)
                && protocol.TryGetInt32(out int version) && version == HelperProtocol.Version;
            if (!versionOk)
            {
                session.Fault(EditorCrashReason.BridgeFault, "The helper speaks a different control protocol version.");
                return false;
            }

            bool sandbox = root.TryGetProperty("sandbox", out JsonElement sb) && sb.ValueKind == JsonValueKind.True;
            if (!sandbox && !_options.AllowNoSandbox)
            {
                session.Fault(EditorCrashReason.BridgeFault, "The helper runs without the WebKit sandbox and the operator did not allow that.");
                return false;
            }

            session.HelloSeen = true;
            return true;
        }

        if (!session.HelloSeen)
        {
            session.Fault(EditorCrashReason.BridgeFault, "The helper's first line was not helper.hello.");
            return false;
        }

        switch (type)
        {
            case HelperProtocol.Pong:
                session.MarkPong(_time.GetTimestamp());
                return true;
            case HelperProtocol.WindowClosed:
                Raise(WindowClosed);
                return true;
            case HelperProtocol.Fatal:
                string code = "unknown";
                string message = string.Empty;
                using (JsonDocument document = JsonDocument.Parse(line))
                {
                    if (document.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                    {
                        code = c.GetString() ?? code;
                    }

                    if (document.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                    {
                        message = m.GetString() ?? message;
                    }
                }

                session.Fault(EditorCrashReason.RendererExited, $"The helper reported {code}: {message}");
                return false;
            default:
                session.Fault(EditorCrashReason.BridgeFault, "The helper sent an unknown control message.");
                return false;
        }
    }

    // The page is ready: replay what the proxy knows, then open the gate for new operations.
    private async Task<bool> OnReadyAsync(Session session)
    {
        if (session.IsReady)
        {
            session.Fault(EditorCrashReason.BridgeFault, "The editor sent ready twice.");
            return false;
        }

        await _sendLock.WaitAsync(session.Token).ConfigureAwait(false);
        try
        {
            string? theme;
            EditorWindowPlacement? window;
            List<(string Key, DocumentState State)> documents;
            lock (_stateLock)
            {
                theme = _themeJson;
                window = _window;
                documents = _openOrder.Where(_documents.ContainsKey).Select(k => (k, _documents[k])).ToList();
            }

            if (window is not null)
            {
                await WriteAsync(session, WindowLine(window), session.Token).ConfigureAwait(false);
            }

            if (theme is not null)
            {
                await SendHostAsync(session, seq => new SetThemeMessage(seq, theme), session.Token).ConfigureAwait(false);
            }

            foreach ((string key, DocumentState state) in documents)
            {
                try
                {
                    string content;
                    bool readOnly;
                    lock (_stateLock)
                    {
                        content = state.Content;
                        readOnly = state.ReadOnly;
                    }

                    EditorDocument document = state.Document with { ReadOnly = readOnly };
                    await SendHostAsync(session, seq => ToOpen(seq, key, content, document), session.Token).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    // The latest content no longer fits the payload limit; the draft written at the crash keeps it.
                }
            }

            session.MarkReady(_time.GetTimestamp());
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }

        lock (_stateLock)
        {
            _lastFailure = null;
        }

        SetState(EditorHelperState.Running);
        _firstReady.TrySetResult();
        Raise(Ready);
        return true;
    }

    private void Dispatch(EditorMessage message)
    {
        switch (message)
        {
            case ChangedMessage changed:
                EditorDocumentId? id = null;
                lock (_stateLock)
                {
                    if (_documents.TryGetValue(changed.DocumentId, out DocumentState? state))
                    {
                        state.Content = changed.Content;
                        state.Dirty = changed.Dirty;
                        id = state.Document.Id;
                    }
                }

                if (id is { } known)
                {
                    Raise(Changed, new EditorChange(known, changed.Version, changed.Content, changed.Dirty));
                }

                break;
            case SaveRequestedMessage save:
                EditorDocumentId? saveId = null;
                lock (_stateLock)
                {
                    if (_documents.TryGetValue(save.DocumentId, out DocumentState? state))
                    {
                        saveId = state.Document.Id;
                    }
                }

                if (saveId is { } known2)
                {
                    Raise(SaveRequested, known2);
                }

                break;
            case KeyPressedMessage key:
                Raise(KeyPressed, new EditorKeyEvent(key.Key, key.Shift, key.Alt, key.Meta, key.Ctrl));
                break;
            case ErrorMessage error:
                Raise(ErrorReported, error);
                break;
            default:
                // Selection moves have no consumer yet.
                break;
        }
    }

    // Pings and the handshake timeout. A silent helper is treated as hung.
    private async Task WatchdogAsync(Session session)
    {
        try
        {
            Task ready = session.ReadySignal.Task;
            Task first = await Task.WhenAny(ready, Task.Delay(_options.HandshakeTimeout, _time, session.Token)).ConfigureAwait(false);
            if (session.Token.IsCancellationRequested)
            {
                return;
            }

            if (first != ready)
            {
                session.Fault(EditorCrashReason.Unknown, "The helper did not finish the handshake in time.");
                return;
            }

            if (_options.PingInterval <= TimeSpan.Zero)
            {
                return;
            }

            using var timer = new PeriodicTimer(_options.PingInterval, _time);
            long id = 0;
            while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false))
            {
                if (_time.GetElapsedTime(session.LastPongTimestamp) > _options.PingTimeout)
                {
                    session.Fault(EditorCrashReason.Unknown, "The helper did not answer a ping in time.");
                    return;
                }

                await _sendLock.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    await WriteAsync(session, HelperProtocol.BuildPing(++id), session.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
    }

    // Encodes with the next sequence number and writes. The caller holds the send lock.
    private async Task SendHostAsync(Session session, Func<long, HostMessage> build, CancellationToken cancellationToken)
    {
        long seq = session.NextSeq;
        string json = _codec.Encode(build(seq));
        session.NextSeq = seq + 1;
        await WriteAsync(session, json, cancellationToken).ConfigureAwait(false);
    }

    // One write, bounded by the write timeout. A helper that cannot take a line in time is hung: kill it. A failed write
    // is not thrown to the caller; the read loop sees the helper end and the supervisor restarts it.
    private async Task WriteAsync(Session session, string line, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Token, timeout.Token);
        try
        {
            await session.Connection.WriteLineAsync(line, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !session.Token.IsCancellationRequested)
        {
            session.Fault(EditorCrashReason.Unknown, "A write to the helper timed out.");
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The session ended while writing.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            session.Fault(EditorCrashReason.RendererExited, "A write to the helper failed: " + ex.Message);
        }
    }

    // Dirty documents go to the draft store when the helper dies (doc 57 section 5). Best effort: the in-memory copy stays.
    private async Task PersistDraftsAsync()
    {
        if (_drafts is null)
        {
            return;
        }

        List<EditorDraft> pending = [];
        DateTimeOffset now = _time.GetUtcNow();
        lock (_stateLock)
        {
            foreach (DocumentState state in _documents.Values)
            {
                if (state.Dirty || !string.Equals(state.Content, state.Document.Content, StringComparison.Ordinal))
                {
                    pending.Add(new EditorDraft(state.Document.Id, state.Content, state.Document.Revision, now));
                }
            }
        }

        foreach (EditorDraft draft in pending)
        {
            try
            {
                await _drafts.WriteAsync(draft, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The draft store is best effort here; the document is still in memory and replays on restart.
            }
        }
    }

    // A subscriber's exception must not end the read loop, so it is contained here.
    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordHandlerFailure(ex);
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T argument)
    {
        try
        {
            handler?.Invoke(this, argument);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordHandlerFailure(ex);
        }
    }

    private void RecordHandlerFailure(Exception ex)
    {
        lock (_stateLock)
        {
            _lastFailure = "An event handler threw: " + ex.Message;
        }
    }

    private sealed class DocumentState
    {
        public DocumentState(EditorDocument document, string content, bool readOnly)
        {
            Document = document;
            Content = content;
            ReadOnly = readOnly;
        }

        public EditorDocument Document { get; }

        public string Content { get; set; }

        public bool ReadOnly { get; set; }

        public bool Dirty { get; set; }
    }

    private readonly record struct SessionOutcome(EditorCrashReason Reason, bool WasReady, TimeSpan Lifetime, string Detail);

    // One helper process and everything that belongs to it: the sequence guard, the host sequence counter, the faults.
    private sealed class Session
    {
        private readonly CancellationTokenSource _cts;
        private int _faulted;
        private long _lastPong;

        public Session(IEditorHelperConnection connection, CancellationToken lifetime)
        {
            Connection = connection;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        }

        public IEditorHelperConnection Connection { get; }

        public BridgeSequenceGuard Guard { get; } = new();

        public CancellationToken Token => _cts.Token;

        public TaskCompletionSource ReadySignal { get; } = NewSignal();

        public TaskCompletionSource Ended { get; } = NewSignal();

        public long NextSeq { get; set; } = 1;

        public bool HelloSeen { get; set; }

        public bool IsReady => Volatile.Read(ref _isReady);

        public bool WasReady { get; private set; }

        public EditorCrashReason? FaultReason { get; private set; }

        public string? FaultDetail { get; private set; }

        public long LastPongTimestamp => Interlocked.Read(ref _lastPong);

        private bool _isReady;

        public void MarkReady(long timestamp)
        {
            Interlocked.Exchange(ref _lastPong, timestamp);
            WasReady = true;
            Volatile.Write(ref _isReady, true);
            ReadySignal.TrySetResult();
        }

        public void MarkPong(long timestamp) => Interlocked.Exchange(ref _lastPong, timestamp);

        /// <summary>Records why the helper is being ended (the first reason wins) and kills it.</summary>
        public void Fault(EditorCrashReason reason, string detail)
        {
            if (Interlocked.CompareExchange(ref _faulted, 1, 0) == 0)
            {
                FaultReason = reason;
                FaultDetail = detail;
            }

            Connection.Kill();
        }

        public Task Cancel() => _cts.CancelAsync();
    }
}
