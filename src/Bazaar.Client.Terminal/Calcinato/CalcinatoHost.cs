using System.Threading.Channels;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calcinato.Session;

namespace Bazaar.Client.Terminal.Calcinato;

/// <summary>
/// Runs Calcinato in the text-mode head: one loop that reads keys from the console, feeds the session manager, polls
/// the console size, and redraws (coalesced) whenever a session changes. The host owns the manager and disposes it when
/// <see cref="RunAsync"/> returns.
/// </summary>
/// <remarks>
/// The loop ends when the last session closes, when the console input fails, or when the token is cancelled. The
/// console is restored on every exit path: alternate screen left, cursor shown, Ctrl+C handling put back.
/// </remarks>
public sealed class CalcinatoHost : IAsyncDisposable
{
    /// <summary>How often the console size is checked while nothing else happens.</summary>
    public static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(250);

    private const string _paneTitle = "Terminal";

    private readonly IConsoleSurface _console;
    private readonly TerminalSessionManager _manager;
    private readonly TimeProvider _timeProvider;
    private readonly AnsiScreenRenderer _renderer = new();
    private readonly ConsoleKeyTranslator _translator = new();
    private readonly TerminalPaneViewModel _pane;
    private readonly Channel<ConsoleKeyInfo> _keys = Channel.CreateUnbounded<ConsoleKeyInfo>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    private ConsoleSize _size;
    private bool _disposed;

    /// <summary>Creates the host.</summary>
    /// <param name="console">The console to draw on and read keys from.</param>
    /// <param name="manager">Session manager. The host takes ownership and disposes it.</param>
    /// <param name="timeProvider">Clock for the resize poll and the pane. Null uses the system clock.</param>
    public CalcinatoHost(IConsoleSurface console, TerminalSessionManager manager, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(manager);
        _console = console;
        _manager = manager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pane = new TerminalPaneViewModel(_paneTitle, manager, _timeProvider);
        _manager.Changed += OnManagerChanged;
    }

    /// <summary>
    /// Opens the first local shell, takes over the console and runs until the last session closes.
    /// </summary>
    /// <param name="shell">Shell path or name for the first session. Null picks the default.</param>
    /// <param name="ct">Stops the loop. Returns 0 on cancellation.</param>
    /// <returns>0 when the user closed everything, or when cancelled.</returns>
    /// <exception cref="NotSupportedException">The head has no local backend.</exception>
    /// <exception cref="Exception">The console input failed; the original exception is rethrown.</exception>
    public async Task<int> RunAsync(string? shell, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _manager.OpenLocal(shell);
        bool previousControlC = _console.TreatControlCAsInput;
        _console.TreatControlCAsInput = true;
        Thread reader = new(() => ReadKeys(_console, _keys.Writer, _wake.Writer))
        {
            IsBackground = true,
            Name = "calcinato-keys",
        };
        reader.Start();

        using ITimer resizeTimer = _timeProvider.CreateTimer(
            _ => _wake.Writer.TryWrite(true),
            null,
            ResizePollInterval,
            ResizePollInterval);

        try
        {
            _console.Write(_renderer.EnterScreen());
            _size = _console.WindowSize;
            await ResizeSessionsAsync(ct).ConfigureAwait(false);
            Draw();

            while (true)
            {
                await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);

                bool inputFailed = await DrainKeysAsync(ct).ConfigureAwait(false);
                if (inputFailed || _manager.Sessions.Count == 0)
                {
                    if (inputFailed)
                    {
                        await _keys.Reader.Completion.ConfigureAwait(false);
                    }

                    return 0;
                }

                ConsoleSize size = _console.WindowSize;
                if (size != _size)
                {
                    _size = size;
                    _renderer.Invalidate();
                    await ResizeSessionsAsync(ct).ConfigureAwait(false);
                }

                Draw();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            _console.Write(_renderer.LeaveScreen());
            _console.TreatControlCAsInput = previousControlC;
            await _manager.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops following the manager and disposes the pane. The manager is disposed by <see cref="RunAsync"/>.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _manager.Changed -= OnManagerChanged;
        _pane.Dispose();
        return ValueTask.CompletedTask;
    }

    // Returns true when the key reader has failed and the channel completed with an error.
    private async Task<bool> DrainKeysAsync(CancellationToken ct)
    {
        while (_keys.Reader.TryRead(out ConsoleKeyInfo info))
        {
            TerminalKey? key = _translator.Translate(info);
            if (key is { } translated)
            {
                await _manager.HandleKeyAsync(translated, ct).ConfigureAwait(false);
            }

            if (_manager.Sessions.Count == 0)
            {
                return false;
            }
        }

        return _keys.Reader.Completion.IsCompleted && _keys.Reader.Completion.IsFaulted;
    }

    private async Task ResizeSessionsAsync(CancellationToken ct)
    {
        int paneRows = Math.Max(1, _size.Rows - 1);
        foreach (TerminalSession session in _manager.Sessions)
        {
            await session.ResizeAsync(_size.Columns, paneRows, ct).ConfigureAwait(false);
        }
    }

    private void Draw()
    {
        TerminalPaneSnapshot snapshot = _pane.Snapshot;
        string? message = _manager.Message;
        if (!string.IsNullOrEmpty(message))
        {
            string status = snapshot.StatusLine.Length == 0 ? message : $"{snapshot.StatusLine}  {message}";
            snapshot = snapshot with { StatusLine = status };
        }

        string frame = _renderer.Render(snapshot, _size.Columns, _size.Rows);
        if (frame.Length > 0)
        {
            _console.Write(frame);
        }
    }

    private void OnManagerChanged(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);

    private static void ReadKeys(IConsoleSurface console, ChannelWriter<ConsoleKeyInfo> keys, ChannelWriter<bool> wake)
    {
        try
        {
            while (true)
            {
                keys.TryWrite(console.ReadKey());
                wake.TryWrite(true);
            }
        }
        catch (Exception ex)
        {
            keys.TryComplete(ex);
            wake.TryWrite(true);
        }
    }
}
