using System.Buffers;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// The chunk buffers of the streamed token amount arithmetic (doc 48, DECIDED 11.4), rented from <see cref="ArrayPool{T}"/>
/// and counted. <see cref="Outstanding"/> and <see cref="Peak"/> show how many chunk buffers are held at once, which is how
/// the "one chunk per operand" memory rule is checked without measuring the garbage collector.
/// </summary>
/// <remarks>Thread-safe. A lease returns its array once, however often it is disposed.</remarks>
public sealed class TokenAmountBufferPool
{
    private int _outstanding;
    private int _peak;

    /// <summary>The process-wide pool.</summary>
    public static TokenAmountBufferPool Shared { get; } = new();

    /// <summary>Buffers rented and not yet disposed.</summary>
    public int Outstanding => Volatile.Read(ref _outstanding);

    /// <summary>The most buffers held at once since creation or the last <see cref="ResetPeak"/>.</summary>
    public int Peak => Volatile.Read(ref _peak);

    /// <summary>Sets <see cref="Peak"/> to the current <see cref="Outstanding"/> count.</summary>
    public void ResetPeak() => Volatile.Write(ref _peak, Outstanding);

    /// <summary>Rents a buffer of exactly <paramref name="length"/> bytes. Its contents are not cleared.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public IMemoryOwner<byte> Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var array = ArrayPool<byte>.Shared.Rent(length);
        var now = Interlocked.Increment(ref _outstanding);
        var peak = Volatile.Read(ref _peak);
        while (now > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, now, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }

        return new Lease(this, array, length);
    }

    private sealed class Lease(TokenAmountBufferPool pool, byte[] array, int length) : IMemoryOwner<byte>
    {
        private byte[]? _array = array;

        public Memory<byte> Memory =>
            (_array ?? throw new ObjectDisposedException(nameof(TokenAmountBufferPool))).AsMemory(0, length);

        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref _array, null);
            if (returned is not null)
            {
                ArrayPool<byte>.Shared.Return(returned);
                Interlocked.Decrement(ref pool._outstanding);
            }
        }
    }
}
