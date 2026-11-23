using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Sources;

/// <summary>
/// Adapts a local <see cref="IPtySession"/> to <see cref="ITerminalSource"/>. The person always has the writer role on
/// their own shell. Output uses a bounded channel that applies back-pressure to the PTY reader, so no output is lost.
/// </summary>
public sealed class LocalTerminalSource : ITerminalSource
{
    private readonly IPtySession _session;
    private readonly Channel<TerminalChunk> _channel;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private int _disposed;

    /// <summary>Wraps a PTY session. Disposing the source ends the session.</summary>
    /// <param name="session">The PTY session to adapt.</param>
    /// <param name="capacity">Output chunks buffered before the PTY reader waits.</param>
    public LocalTerminalSource(IPtySession session, int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _session = session;
        _channel = Channel.CreateBounded<TerminalChunk>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = PumpAsync(_pumpCts.Token);
    }

    /// <inheritdoc />
    public bool IsRemote => false;

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public ChannelReader<TerminalChunk> Output => _channel.Reader;

    /// <inheritdoc />
    public Task<int> Exited => _session.Exited;

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => _session.WriteAsync(bytes, ct);

    /// <inheritdoc />
    public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _session.Resize(cols, rows);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _session.DisposeAsync().ConfigureAwait(false);
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the pump was cancelled during disposal.
        }

        _pumpCts.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in _session.Output.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _channel.Writer.WriteAsync(new TerminalChunk(0, bytes), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposal ends the pump.
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}
