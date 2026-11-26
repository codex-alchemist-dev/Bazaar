using System.Collections.Concurrent;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Emulation;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Session;

/// <summary>Event data for <see cref="TerminalSession.Exited"/>.</summary>
public sealed class TerminalExitedEventArgs(int exitCode) : EventArgs
{
    /// <summary>The exit code the source reported. -1 when the source ended without one.</summary>
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// One terminal the person can see: an <see cref="ITerminalSource"/> with its <see cref="Emulator"/> and
/// <see cref="ModalEngine"/> (doc 55, sections 5.2 and 5.3). A background pump feeds source output into the emulator.
/// Device replies and modal bytes go back to the source only while it is writable. Replayed output is fed like live
/// output, but its device replies are dropped so the shell never sees answers to queries it has already had answered.
/// </summary>
/// <remarks>
/// <see cref="HandleKeyAsync"/> and <see cref="ResizeAsync"/> are called from the head's UI thread. The emulator and the
/// engine are guarded by <see cref="SyncRoot"/>, so a view may read them from any thread while holding it. Events are
/// raised on the thread that caused them (the pump thread for output); the head marshals them to its UI thread.
/// </remarks>
public sealed class TerminalSession : IAsyncDisposable
{
    private const string _readOnlyStatus = "Read-only session: input is not sent.";

    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentQueue<byte[]> _responses = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private bool _suppressResponses;
    private int? _exitCode;
    private int _disposed;

    /// <summary>Creates a session and starts its output pump.</summary>
    /// <param name="id">Session identifier, unique within a manager.</param>
    /// <param name="source">The byte stream. The session owns it and disposes it.</param>
    /// <param name="timeProvider">Clock for the emulator's device-reply rate limit.</param>
    /// <param name="cols">Initial columns.</param>
    /// <param name="rows">Initial rows.</param>
    /// <param name="mnemonics">Hands typed sequences to the client mnemonic router. Null means none.</param>
    /// <param name="scrollbackLimit">Scrollback lines kept above the screen.</param>
    public TerminalSession(
        string id,
        ITerminalSource source,
        TimeProvider timeProvider,
        int cols,
        int rows,
        IMnemonicBridge? mnemonics = null,
        int scrollbackLimit = 10000)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Id = id;
        Source = source;
        Emulator = new Emulator(cols, rows, scrollbackLimit, timeProvider);
        Emulator.Response += OnResponse;
        Engine = new ModalEngine(Emulator, mnemonics, applicationCursorKeys: () => Emulator.ApplicationCursorKeys)
        {
            CanWrite = source.CanWrite,
        };
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Raised once per batch of output and after each key press, resize or status change. Coalesced per batch.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the terminal ends, with its exit code.</summary>
    public event EventHandler<TerminalExitedEventArgs>? Exited;

    /// <summary>Raised when the engine asks for an ex command (<c>:q</c>, <c>:new</c>, ...). The manager carries it out.</summary>
    public event EventHandler<ExCommand>? CommandRequested;

    /// <summary>Raised with text for the screen reader live region (doc 49).</summary>
    public event EventHandler<string>? SpeechRequested;

    /// <summary>Raised with text the person yanked from a Visual selection, for the client clipboard.</summary>
    public event EventHandler<string>? YankRequested;

    /// <summary>Session identifier.</summary>
    public string Id { get; }

    /// <summary>The byte stream behind this session.</summary>
    public ITerminalSource Source { get; }

    /// <summary>The emulator. Read it under <see cref="SyncRoot"/>.</summary>
    public Emulator Emulator { get; }

    /// <summary>The modal engine. Read and call it under <see cref="SyncRoot"/>.</summary>
    public ModalEngine Engine { get; }

    /// <summary>The lock that guards <see cref="Emulator"/> and <see cref="Engine"/>. Views hold it while they read a snapshot.</summary>
    internal object SyncRoot => _syncRoot;

    /// <summary>The latest status line text (engine status, read-only notices, exit notice).</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>True while the most recent output came from the scrollback ring on attach or resubscribe.</summary>
    public bool IsReplaying { get; private set; }

    /// <summary>True for a server terminal.</summary>
    public bool IsRemote => Source.IsRemote;

    /// <summary>True for a viewer without the writer role. Input is refused.</summary>
    public bool IsReadOnly => !Source.CanWrite;

    /// <summary>The exit code once the terminal has ended, or null while it runs.</summary>
    public int? ExitCode
    {
        get
        {
            lock (_syncRoot)
            {
                return _exitCode;
            }
        }
    }

    /// <summary>True for a local shell that has not exited yet. Closing it needs the person's confirmation.</summary>
    public bool IsRunningLocalProcess => !Source.IsRemote && ExitCode is null;

    /// <summary>Handles one key press: runs the engine, sends its bytes to the source and raises its effects.</summary>
    /// <param name="key">The key press.</param>
    /// <param name="ct">Cancels the write to the source.</param>
    public async ValueTask HandleKeyAsync(TerminalKey key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ModalResult result;
        lock (_syncRoot)
        {
            Engine.CanWrite = Source.CanWrite;
            result = Engine.Handle(key);
        }

        await ApplyEffectsAsync(result.Effects, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>
    /// Sends pasted text to the source. When the program has turned bracketed paste on (DECSET 2004), the text is
    /// wrapped in the paste markers so the shell does not run it line by line. Refused for a read-only viewer.
    /// </summary>
    /// <param name="text">The text to paste.</param>
    /// <param name="ct">Cancels the write to the source.</param>
    /// <returns>False when the source is read-only and nothing was sent.</returns>
    public async ValueTask<bool> PasteAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();

        bool bracketed;
        lock (_syncRoot)
        {
            bracketed = Emulator.BracketedPaste;
        }

        byte[] payload = System.Text.Encoding.UTF8.GetBytes(text);
        if (bracketed)
        {
            byte[] start = "\u001b[200~"u8.ToArray();
            byte[] end = "\u001b[201~"u8.ToArray();
            payload = [.. start, .. payload, .. end];
        }

        bool sent = await WriteIfWritableAsync(payload, ct).ConfigureAwait(false);
        if (!sent)
        {
            SetStatus(_readOnlyStatus);
            RaiseChanged();
        }

        return sent;
    }

    /// <summary>Resizes the emulator and the source together.</summary>
    /// <param name="cols">Columns, at least 1.</param>
    /// <param name="rows">Rows, at least 1.</param>
    /// <param name="ct">Cancels the resize sent to the source.</param>
    public async ValueTask ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        lock (_syncRoot)
        {
            Emulator.Resize(cols, rows);
        }

        await Source.ResizeAsync(cols, rows, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>Stops the pump, disposes the source and ends the shell. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The pump observed the cancellation requested above.
        }

        Emulator.Response -= OnResponse;
        await Source.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            ChannelReader<TerminalChunk> output = Source.Output;
            while (await output.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                bool fed = false;
                while (output.TryRead(out TerminalChunk chunk))
                {
                    Feed(chunk);
                    fed = true;
                }

                await FlushResponsesAsync(ct).ConfigureAwait(false);
                if (fed)
                {
                    RaiseChanged();
                }
            }

            int code = await Source.Exited.WaitAsync(ct).ConfigureAwait(false);
            lock (_syncRoot)
            {
                _exitCode = code;
                StatusText = $"Exited with code {code}.";
            }

            Exited?.Invoke(this, new TerminalExitedEventArgs(code));
            RaiseChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed: nothing left to report.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus($"Terminal output failed: {ex.Message}");
            RaiseChanged();
        }
    }

    private void Feed(TerminalChunk chunk)
    {
        lock (_syncRoot)
        {
            IsReplaying = chunk.IsReplay;
            _suppressResponses = chunk.IsReplay;
            try
            {
                Emulator.Feed(chunk.Bytes.Span);
            }
            finally
            {
                _suppressResponses = false;
            }

            Engine.CanWrite = Source.CanWrite;
        }
    }

    // Called from Emulator.Feed while the sync root is held.
    private void OnResponse(ReadOnlyMemory<byte> reply)
    {
        if (!_suppressResponses)
        {
            _responses.Enqueue(reply.ToArray());
        }
    }

    private async Task FlushResponsesAsync(CancellationToken ct)
    {
        while (_responses.TryDequeue(out byte[]? reply))
        {
            await WriteIfWritableAsync(reply, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyEffectsAsync(IReadOnlyList<ModalEffect> effects, CancellationToken ct)
    {
        foreach (ModalEffect effect in effects)
        {
            switch (effect)
            {
                case ModalEffect.SendBytes send:
                    if (!await WriteIfWritableAsync(send.Bytes, ct).ConfigureAwait(false))
                    {
                        SetStatus(_readOnlyStatus);
                    }

                    break;
                case ModalEffect.StatusLine status:
                    SetStatus(status.Text);
                    break;
                case ModalEffect.Speech speech:
                    SpeechRequested?.Invoke(this, speech.Text);
                    break;
                case ModalEffect.Yank yank:
                    YankRequested?.Invoke(this, yank.Text);
                    break;
                case ModalEffect.Command command:
                    CommandRequested?.Invoke(this, command.Request);
                    break;
                default:
                    // ModeChanged, ScrollTo, CursorMoved and GhostText describe engine state that the view reads directly.
                    break;
            }
        }
    }

    private async ValueTask<bool> WriteIfWritableAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (!Source.CanWrite)
        {
            return false;
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Source.CanWrite)
            {
                return false;
            }

            await Source.WriteAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void SetStatus(string text)
    {
        lock (_syncRoot)
        {
            StatusText = text;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
