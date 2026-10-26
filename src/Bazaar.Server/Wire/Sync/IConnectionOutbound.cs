using System.Threading.Channels;
using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// A connection's bounded outbound queue (doc 02, backpressure). The connection manager (W3) drains it onto the
/// socket. Live events never wait: a full queue means the stream lags. Replay waits for room.
/// </summary>
public interface IConnectionOutbound
{
    /// <summary>Queues a live event; false when the queue is full.</summary>
    bool TryEnqueue(in Envelope envelope);

    /// <summary>Queues a replay event, waiting for room.</summary>
    ValueTask EnqueueAsync(Envelope envelope, CancellationToken ct);

    /// <summary>Queues a control frame (<c>lagged</c>, <c>stream.revoked</c>, <c>resync</c>) past the bound.</summary>
    void EnqueueControl(Envelope envelope);
}

/// <summary>Default <see cref="IConnectionOutbound"/>: an unbounded channel with a depth counter that enforces the bound.</summary>
public sealed class BoundedOutbound : IConnectionOutbound, IDisposable
{
    private readonly Channel<Envelope> _channel = Channel.CreateUnbounded<Envelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _space = new(0);
    private readonly int _max;
    private int _depth;
    private int _waiters;

    /// <summary>Creates a queue that holds at most <paramref name="max"/> non-control frames.</summary>
    public BoundedOutbound(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        _max = max;
    }

    /// <summary>Frames currently queued.</summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <inheritdoc />
    public bool TryEnqueue(in Envelope envelope)
    {
        if (Interlocked.Increment(ref _depth) > _max)
        {
            Interlocked.Decrement(ref _depth);
            return false;
        }

        _channel.Writer.TryWrite(envelope);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(Envelope envelope, CancellationToken ct)
    {
        if (TryEnqueue(envelope))
        {
            return;
        }

        Interlocked.Increment(ref _waiters);
        try
        {
            // Registering as a waiter before the retry means a reader that dequeues after the retry sees the waiter.
            while (!TryEnqueue(envelope))
            {
                await _space.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }
    }

    /// <inheritdoc />
    public void EnqueueControl(Envelope envelope)
    {
        Interlocked.Increment(ref _depth);
        _channel.Writer.TryWrite(envelope);
    }

    /// <summary>Reads queued frames in order; the connection manager's writer loop.</summary>
    public async IAsyncEnumerable<Envelope> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _depth);
            if (Volatile.Read(ref _waiters) > 0)
            {
                _space.Release();
            }

            yield return envelope;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _space.Dispose();

    /// <summary>Ends the queue; the reader finishes after the frames already queued.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
