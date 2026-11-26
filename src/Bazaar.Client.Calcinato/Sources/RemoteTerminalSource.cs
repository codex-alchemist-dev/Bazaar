using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Contracts.Terminal;

namespace Bazaar.Client.Calcinato.Sources;

/// <summary>
/// A server terminal attached over <see cref="ITerminalWire"/>. Sequence numbers are checked on every output frame:
/// a gap triggers one <see cref="TerminalReplay"/> from the first missing sequence, replayed chunks are flagged
/// <see cref="TerminalChunk.IsReplay"/>, duplicates are dropped. The output buffer is bounded and drops its oldest
/// chunk on overflow; the lost range is then requested again.
/// </summary>
/// <remarks>
/// Sequence state is touched only by the pump task. Write, resize and control calls run on the caller's thread and
/// read only the volatile role flags.
/// </remarks>
public sealed class RemoteTerminalSource : ITerminalSource
{
    private readonly ITerminalWire _wire;
    private readonly string _terminalId;
    private readonly Channel<TerminalChunk> _channel;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pump = Task.CompletedTask;
    private volatile bool _canWrite;
    private volatile bool _ended;
    private int _disposed;

    // Sequence state. Pump task only.
    private long _expected = 1;
    private bool _windowOpen;
    private long _windowNext;
    private long _windowTo;
    private long _overflowFrom;
    private long _overflowTo;

    /// <summary>Creates a source for one terminal. Call <see cref="OpenAsync"/> before reading.</summary>
    /// <param name="wire">The wire view of this terminal.</param>
    /// <param name="terminalId">Terminal id; frames for other ids are ignored.</param>
    /// <param name="capacity">Output chunks buffered before the oldest is dropped.</param>
    public RemoteTerminalSource(ITerminalWire wire, string terminalId, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentException.ThrowIfNullOrEmpty(terminalId);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _wire = wire;
        _terminalId = terminalId;
        _channel = Channel.CreateBounded<TerminalChunk>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        }, OnChunkDropped);
    }

    /// <summary>Raised on the pump thread for every status change. Handlers must return quickly.</summary>
    public event EventHandler<TerminalSourceStatus>? StatusChanged;

    /// <inheritdoc />
    public bool IsRemote => true;

    /// <inheritdoc />
    public bool CanWrite => _canWrite && !_ended;

    /// <inheritdoc />
    public ChannelReader<TerminalChunk> Output => _channel.Reader;

    /// <inheritdoc />
    public Task<int> Exited => _exited.Task;

    /// <summary>Starts the frame pump, sends <see cref="TerminalOpen"/> and waits for <see cref="TerminalOpened"/>.</summary>
    /// <param name="cols">Initial columns.</param>
    /// <param name="rows">Initial rows.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <exception cref="InvalidOperationException">The server refused the attach or the wire closed first.</exception>
    public async Task OpenAsync(int cols, int rows, CancellationToken ct)
    {
        ThrowIfDisposed();
        _pump = PumpAsync(_pumpCts.Token);
        await _wire.SendAsync(new TerminalOpen(_terminalId, AgentId: null, cols, rows), ct).ConfigureAwait(false);
        await _opened.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the server to replay from the next sequence after the last one seen. Call after the connection comes
    /// back. Restarts the pump if the frame stream closed.
    /// </summary>
    public async ValueTask ResubscribeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_pump.IsCompleted && !_pumpCts.IsCancellationRequested)
        {
            _pump = PumpAsync(_pumpCts.Token);
        }

        var from = _expected;
        RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.ReplayRequested, from.ToString(System.Globalization.CultureInfo.InvariantCulture), "resubscribe"));
        await _wire.SendAsync(new TerminalReplay(_terminalId, from), ct).ConfigureAwait(false);
    }

    /// <summary>Asks for the writer role. The answer arrives as a control frame and updates <see cref="CanWrite"/>.</summary>
    public ValueTask TakeAsync(CancellationToken ct) => SendControlAsync("take", ct);

    /// <summary>Gives up the writer role. <see cref="CanWrite"/> turns false at once.</summary>
    public ValueTask ReleaseAsync(CancellationToken ct)
    {
        _canWrite = false;
        return SendControlAsync("release", ct);
    }

    /// <inheritdoc />
    /// <remarks>Input for a read-only or failed source is refused with a status event and nothing is sent.</remarks>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (!CanWrite)
        {
            RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.WriteRefused));
            return;
        }

        await _wire.SendAsync(new TerminalInput(_terminalId, bytes.ToArray()), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Only the writer sends size changes. Viewers are ignored silently.</remarks>
    public async ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        if (!CanWrite)
        {
            return;
        }

        await _wire.SendAsync(new TerminalResize(_terminalId, cols, rows), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sends <see cref="TerminalClose"/> when the terminal has not exited, stops the pump and completes output. An
    /// exit that never arrived is reported as code -1.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var wasEnded = _ended;
        _ended = true;
        _canWrite = false;
        if (!wasEnded)
        {
            try
            {
                await _wire.SendAsync(new TerminalClose(_terminalId), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Detaching is best effort; the server drops the attach when the wire closes.
            }
        }

        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by disposal.
        }

        _channel.Writer.TryComplete();
        _exited.TrySetResult(-1);
        _pumpCts.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private ValueTask SendControlAsync(string action, CancellationToken ct)
    {
        if (_ended)
        {
            return ValueTask.CompletedTask;
        }

        return _wire.SendAsync(new TerminalControl(_terminalId, action), ct);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _wire.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (frame.TerminalId == _terminalId)
                {
                    await HandleAsync(frame, ct).ConfigureAwait(false);
                }
            }

            if (!_ended)
            {
                _canWrite = false;
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Disconnected));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal stops the pump.
        }
        catch (Exception ex)
        {
            // Fail closed: nothing else is sent to the server after a pump failure.
            _canWrite = false;
            RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Error, "pump", ex.Message));
            _opened.TrySetException(ex);
        }
        finally
        {
            _opened.TrySetException(new InvalidOperationException("The wire closed before the terminal opened."));
        }
    }

    private async Task HandleAsync(TerminalFrame frame, CancellationToken ct)
    {
        switch (frame)
        {
            case TerminalOpened opened:
                _expected = Math.Max(_expected, Math.Max(1, opened.ReplayFrom));
                _canWrite = !opened.WriterHeld;
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Opened));
                _opened.TrySetResult(true);
                break;

            case TerminalOutput output:
                await HandleOutputAsync(output.Seq, output.Bytes, ct).ConfigureAwait(false);
                break;

            case TerminalControl control:
                HandleControl(control.Action);
                break;

            case TerminalExit exit:
                _ended = true;
                _canWrite = false;
                _channel.Writer.TryComplete();
                _exited.TrySetResult(exit.Code);
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Exited, exit.Code.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;

            case TerminalError error:
                // Fail closed: input stops, output keeps flowing so the person can read what is there.
                _canWrite = false;
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Error, error.Code, error.Message));
                _opened.TrySetException(new InvalidOperationException($"Terminal error {error.Code}: {error.Message}"));
                break;
        }
    }

    private void HandleControl(string action)
    {
        switch (action)
        {
            case "granted":
                _canWrite = true;
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.ControlChanged, "granted"));
                break;
            case "denied":
                RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.ControlChanged, "denied"));
                break;
        }
    }

    private async Task HandleOutputAsync(long seq, byte[] bytes, CancellationToken ct)
    {
        if (seq < 1)
        {
            return;
        }

        // A dropped range is requested once, before the next frame is handled.
        if (_overflowFrom != 0 && !_windowOpen)
        {
            var from = _overflowFrom;
            var to = _overflowTo;
            _overflowFrom = 0;
            _overflowTo = 0;
            await OpenWindowAsync(from, to, ct).ConfigureAwait(false);
        }

        if (_windowOpen)
        {
            // Only the next replayed sequence inside the window is accepted. Everything else (duplicates, frames that
            // came before the replay, and live frames the replay will resend) is dropped.
            if (seq == _windowNext && seq <= _windowTo)
            {
                Deliver(seq, bytes, isReplay: true);
                _windowNext++;
                _expected = Math.Max(_expected, _windowNext);
                if (_windowNext > _windowTo)
                {
                    _windowOpen = false;
                }
            }

            return;
        }

        if (seq < _expected)
        {
            return;
        }

        if (seq == _expected)
        {
            Deliver(seq, bytes, isReplay: false);
            _expected++;
            return;
        }

        // Gap: request [expected, seq] and drop this frame. The replay sends it again in order.
        await OpenWindowAsync(_expected, seq, ct).ConfigureAwait(false);
    }

    private async Task OpenWindowAsync(long from, long to, CancellationToken ct)
    {
        _windowOpen = true;
        _windowNext = from;
        _windowTo = to;
        RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.ReplayRequested, from.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await _wire.SendAsync(new TerminalReplay(_terminalId, from), ct).ConfigureAwait(false);
    }

    private void Deliver(long seq, byte[] bytes, bool isReplay)
    {
        // A closed channel (after exit or dispose) refuses the write, and the chunk is ignored.
        _channel.Writer.TryWrite(new TerminalChunk(seq, bytes, isReplay));
    }

    private void OnChunkDropped(TerminalChunk dropped)
    {
        if (dropped.Seq < 1)
        {
            return;
        }

        if (_overflowFrom == 0)
        {
            _overflowFrom = dropped.Seq;
        }

        _overflowTo = dropped.Seq;
        RaiseStatus(new TerminalSourceStatus(TerminalStatusKind.Overflow, dropped.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void RaiseStatus(TerminalSourceStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }
}
